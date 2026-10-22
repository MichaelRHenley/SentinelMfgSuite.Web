using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// End-to-end report lifecycle through the real services against PostgreSQL (no direct SQL seeding of lifecycle state).
/// Content validation uses a passing validator because rule coverage lives in the validator tests; this class verifies
/// the cooperation of content editing, transitions, correction creation, replacement issuance and database enforcement.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
public sealed class AssessmentReportLifecycleEndToEndPostgresIntegrationTests
{
    private static readonly DateTimeOffset T1 = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PassingValidator : IAssessmentReportContentValidator
    {
        public AssessmentReportContentValidationResult Validate(
            ConsultantReportContent content, AssessmentReportContentValidationContext context)
            => new(Array.Empty<ReportContentValidationIssue>());
    }

    private static async Task<T> LifecycleAsync<T>(PostgresIntegrationTestFixture fixture, DateTimeOffset at, Func<AssessmentReportLifecycleService, Task<T>> action)
    {
        await using var db = fixture.CreateDbContext();
        return await action(new AssessmentReportLifecycleService(db, new PassingValidator(), new ManualTimeProvider(at)));
    }

    private static async Task<T> ContentAsync<T>(PostgresIntegrationTestFixture fixture, Func<AssessmentReportRevisionContentService, Task<T>> action)
    {
        await using var db = fixture.CreateDbContext();
        return await action(new AssessmentReportRevisionContentService(db, new PassingValidator()));
    }

    private static async Task<AssessmentReportRevision> LoadAsync(PostgresIntegrationTestFixture fixture, Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static ConsultantReportContent ContentWith(string narrative)
        => ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(Narrative: narrative)
        };

    private static async Task<Guid> SaveAsync(PostgresIntegrationTestFixture fixture, Guid revisionId, string narrative, System.Security.Claims.ClaimsPrincipal caller)
    {
        var stamp = (await LoadAsync(fixture, revisionId)).ConcurrencyStamp;
        var saved = await ContentAsync(fixture, s => s.SaveDraftAsync(revisionId, stamp, ContentWith(narrative), caller));
        Assert.Equal(AssessmentReportRevisionContentState.Saved, saved.State);
        return saved.ConcurrencyStamp!.Value;
    }

    private static async Task<Guid> SubmitAndApproveAsync(PostgresIntegrationTestFixture fixture, Guid revisionId, Guid draftStamp, DateTimeOffset at, System.Security.Claims.ClaimsPrincipal caller)
    {
        var submit = await LifecycleAsync(fixture, at, s => s.SubmitForReviewAsync(revisionId, draftStamp, caller));
        Assert.True(submit.Succeeded);
        var approve = await LifecycleAsync(fixture, at, s => s.ApproveAsync(revisionId, submit.ConcurrencyStamp!.Value, caller));
        Assert.True(approve.Succeeded);
        return approve.ConcurrencyStamp!.Value;
    }

    private static async Task<List<AssessmentReportTransitionHistory>> HistoryAsync(PostgresIntegrationTestFixture fixture, Guid reportId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<AssessmentReportTransitionHistory>().AsNoTracking()
            .Where(h => h.AssessmentReportId == reportId).OrderBy(h => h.OccurredUtc).ToListAsync();
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Full_Lifecycle_Initial_Issuance_Then_Correction_Supersedes_Atomically()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);

        // Revision 1: draft content -> review -> approval -> issuance.
        var draftStamp = await SaveAsync(fixture, chain.RevisionId, "original findings", caller);
        var approvedStamp = await SubmitAndApproveAsync(fixture, chain.RevisionId, draftStamp, T1, caller);
        var issued1 = await LifecycleAsync(fixture, T1.AddHours(1), s => s.IssueAsync(chain.RevisionId, approvedStamp, caller));
        Assert.True(issued1.Succeeded);
        Assert.Null(issued1.SupersededRevisionId);
        var rev1Issued = await LoadAsync(fixture, chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, rev1Issued.Status);

        // Issued revisions are no longer editable through the content service.
        var editIssued = await ContentAsync(fixture, s => s.SaveDraftAsync(chain.RevisionId, rev1Issued.ConcurrencyStamp, ContentWith("tamper"), caller));
        Assert.Equal(AssessmentReportRevisionContentState.NotDraft, editIssued.State);

        // Revision 2: correction copies frozen data but has independent editable content.
        var created = await LifecycleAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, rev1Issued.ConcurrencyStamp, "clarify scope", caller));
        Assert.True(created.Succeeded);
        var rev2Id = created.RevisionId!.Value;
        var rev2Draft = await LoadAsync(fixture, rev2Id);
        Assert.Equal(2, rev2Draft.RevisionNumber);
        Assert.Equal(chain.RevisionId, rev2Draft.BasedOnRevisionId);
        Assert.Equal(rev1Issued.ScoringSnapshot, rev2Draft.ScoringSnapshot);
        Assert.Equal(rev1Issued.ConsultantContentJson, rev2Draft.ConsultantContentJson);
        Assert.Null(rev2Draft.IssuedUtc);
        Assert.Null(rev2Draft.ApprovedUtc);
        Assert.Null(rev2Draft.PdfStorageKey);

        var corrDraftStamp = await SaveAsync(fixture, rev2Id, "corrected findings", caller);
        var corrApproved = await SubmitAndApproveAsync(fixture, rev2Id, corrDraftStamp, T1.AddHours(3), caller);
        var issued2 = await LifecycleAsync(fixture, T1.AddHours(4), s => s.IssueAsync(rev2Id, corrApproved, caller));
        Assert.True(issued2.Succeeded);
        Assert.Equal(chain.RevisionId, issued2.SupersededRevisionId);

        // Revision 1 is Superseded and otherwise unchanged; revision 2 is the sole current Issued revision.
        var rev1After = await LoadAsync(fixture, chain.RevisionId);
        var rev2After = await LoadAsync(fixture, rev2Id);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, rev1After.Status);
        Assert.Equal(rev2Id, rev1After.SupersededByRevisionId);
        Assert.Equal(rev1Issued.IssuedUtc, rev1After.IssuedUtc);
        Assert.Equal(rev1Issued.IssuedByUserId, rev1After.IssuedByUserId);
        Assert.Equal(rev1Issued.ApprovedUtc, rev1After.ApprovedUtc);
        Assert.Equal(rev1Issued.ScoringSnapshot, rev1After.ScoringSnapshot);
        Assert.Equal(rev1Issued.ConsultantContentJson, rev1After.ConsultantContentJson);
        Assert.NotEqual(rev1After.ConsultantContentJson, rev2After.ConsultantContentJson);
        Assert.Equal(rev1Issued.ScoringSnapshot, rev2After.ScoringSnapshot);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, rev2After.Status);
        Assert.Equal(chain.RevisionId, rev2After.BasedOnRevisionId);
        Assert.NotNull(rev2After.ApprovedUtc);
        Assert.NotNull(rev2After.IssuedUtc);

        await using (var db = fixture.CreateDbContext())
        {
            var states = await db.Set<AssessmentReportRevision>().AsNoTracking()
                .Where(r => r.AssessmentReportId == chain.ReportId).ToListAsync();
            Assert.Equal(rev2Id, states.Single(r => r.Status == AssessmentReportRevisionStatus.Issued).Id);
            Assert.DoesNotContain(states, r => r.Status is AssessmentReportRevisionStatus.Draft or AssessmentReportRevisionStatus.InReview or AssessmentReportRevisionStatus.Approved);
        }

        // History: created, transitions for both revisions, and reciprocal supersession events sharing one correlation id.
        var history = await HistoryAsync(fixture, chain.ReportId);
        Assert.Contains(history, h => h.AssessmentReportRevisionId == rev2Id && h.EventType == AssessmentReportTransitionEventType.Created);
        var superseded = history.Single(h => h.NewStatus == AssessmentReportRevisionStatus.Superseded);
        var replacementIssued = history.Single(h => h.AssessmentReportRevisionId == rev2Id && h.NewStatus == AssessmentReportRevisionStatus.Issued);
        Assert.Equal(chain.RevisionId, superseded.AssessmentReportRevisionId);
        Assert.Equal(rev2Id, superseded.ReplacementRevisionId);
        Assert.Equal(chain.RevisionId, replacementIssued.ReplacedRevisionId);
        Assert.Equal(superseded.CorrelationId, replacementIssued.CorrelationId);
        Assert.Equal(1, history.Count(h => h.AssessmentReportRevisionId == chain.RevisionId && h.NewStatus == AssessmentReportRevisionStatus.Issued));
        Assert.All(history, h => Assert.Equal(chain.UserId, h.ActorUserId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Rejected_Correction_Frees_The_Slot_And_A_Later_Correction_Succeeds()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);

        var draftStamp = await SaveAsync(fixture, chain.RevisionId, "original", caller);
        var approved = await SubmitAndApproveAsync(fixture, chain.RevisionId, draftStamp, T1, caller);
        Assert.True((await LifecycleAsync(fixture, T1.AddHours(1), s => s.IssueAsync(chain.RevisionId, approved, caller))).Succeeded);
        var source = await LoadAsync(fixture, chain.RevisionId);

        var first = await LifecycleAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, source.ConcurrencyStamp, "first try", caller));
        Assert.True(first.Succeeded);

        // A second correction while one is active is rejected, and the source is untouched.
        var duplicate = await LifecycleAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, source.ConcurrencyStamp, "dup", caller));
        Assert.Equal(AssessmentReportLifecycleState.ActiveCorrectionExists, duplicate.State);

        var firstStamp = await SaveAsync(fixture, first.RevisionId!.Value, "rejected text", caller);
        var submit = await LifecycleAsync(fixture, T1.AddHours(3), s => s.SubmitForReviewAsync(first.RevisionId!.Value, firstStamp, caller));
        Assert.True(submit.Succeeded);
        var reject = await LifecycleAsync(fixture, T1.AddHours(3), s => s.RejectAsync(first.RevisionId!.Value, submit.ConcurrencyStamp!.Value, "not accurate", caller));
        Assert.True(reject.Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Rejected, (await LoadAsync(fixture, first.RevisionId.Value)).Status);

        // Rejected is terminal: it cannot be edited, re-submitted or issued.
        var rejectedStamp = (await LoadAsync(fixture, first.RevisionId.Value)).ConcurrencyStamp;
        Assert.Equal(AssessmentReportRevisionContentState.NotDraft,
            (await ContentAsync(fixture, s => s.SaveDraftAsync(first.RevisionId.Value, rejectedStamp, ContentWith("x"), caller))).State);
        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition,
            (await LifecycleAsync(fixture, T1.AddHours(4), s => s.IssueAsync(first.RevisionId.Value, rejectedStamp, caller))).State);

        // The source is still the current Issued revision and a new correction can be created from it.
        var sourceAfterReject = await LoadAsync(fixture, chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, sourceAfterReject.Status);
        Assert.Equal(source.ConcurrencyStamp, sourceAfterReject.ConcurrencyStamp);

        var second = await LifecycleAsync(fixture, T1.AddHours(5), s => s.CreateCorrectionAsync(chain.ReportId, sourceAfterReject.ConcurrencyStamp, "second try", caller));
        Assert.True(second.Succeeded);
        Assert.Equal(3, second.RevisionNumber);
        var secondId = second.RevisionId!.Value;
        var secondDraft = await SaveAsync(fixture, secondId, "accepted text", caller);
        var secondApproved = await SubmitAndApproveAsync(fixture, secondId, secondDraft, T1.AddHours(6), caller);
        var issued = await LifecycleAsync(fixture, T1.AddHours(7), s => s.IssueAsync(secondId, secondApproved, caller));
        Assert.True(issued.Succeeded);
        Assert.Equal(chain.RevisionId, issued.SupersededRevisionId);

        var rev3 = await LoadAsync(fixture, secondId);
        Assert.Equal(chain.RevisionId, rev3.BasedOnRevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, (await LoadAsync(fixture, chain.RevisionId)).Status);
        Assert.Equal(AssessmentReportRevisionStatus.Rejected, (await LoadAsync(fixture, first.RevisionId.Value)).Status);
        var rejectedRevision = await LoadAsync(fixture, first.RevisionId.Value);
        Assert.Null(rejectedRevision.IssuedUtc);
        Assert.Null(rejectedRevision.SupersededByRevisionId);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Non_Owner_And_Non_Admin_Callers_Cannot_Drive_The_Lifecycle()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var owner = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var stranger = AssessmentReportPostgresIntegrationTests.Admin($"stranger-{Guid.NewGuid():N}");
        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        var stamp = (await LoadAsync(fixture, chain.RevisionId)).ConcurrencyStamp;

        Assert.Equal(AssessmentReportLifecycleState.NotFound, (await LifecycleAsync(fixture, T1, s => s.SubmitForReviewAsync(chain.RevisionId, stamp, stranger))).State);
        Assert.Equal(AssessmentReportLifecycleState.Forbidden, (await LifecycleAsync(fixture, T1, s => s.SubmitForReviewAsync(chain.RevisionId, stamp, anonymous))).State);
        Assert.Equal(AssessmentReportRevisionContentState.NotFound, (await ContentAsync(fixture, s => s.SaveDraftAsync(chain.RevisionId, stamp, ContentWith("x"), stranger))).State);
        Assert.Equal(AssessmentReportRevisionContentState.Forbidden, (await ContentAsync(fixture, s => s.SaveDraftAsync(chain.RevisionId, stamp, ContentWith("x"), anonymous))).State);

        var draftStamp = await SaveAsync(fixture, chain.RevisionId, "owned", owner);
        var approved = await SubmitAndApproveAsync(fixture, chain.RevisionId, draftStamp, T1, owner);
        Assert.True((await LifecycleAsync(fixture, T1.AddHours(1), s => s.IssueAsync(chain.RevisionId, approved, owner))).Succeeded);
        var issuedStamp = (await LoadAsync(fixture, chain.RevisionId)).ConcurrencyStamp;

        Assert.Equal(AssessmentReportLifecycleState.NotFound, (await LifecycleAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, issuedStamp, null, stranger))).State);
        Assert.Equal(AssessmentReportLifecycleState.Forbidden, (await LifecycleAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, issuedStamp, null, anonymous))).State);
        Assert.Single((await HistoryAsync(fixture, chain.ReportId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Draft));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Stale_Stamps_Are_Rejected_At_Every_Lifecycle_Step_And_Change_Nothing()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var stale = Guid.NewGuid();

        Assert.Equal(AssessmentReportRevisionContentState.ConcurrencyConflict, (await ContentAsync(fixture, s => s.SaveDraftAsync(chain.RevisionId, stale, ContentWith("x"), caller))).State);
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, (await LifecycleAsync(fixture, T1, s => s.SubmitForReviewAsync(chain.RevisionId, stale, caller))).State);

        var draftStamp = await SaveAsync(fixture, chain.RevisionId, "v1", caller);
        // The old stamp is stale after a save.
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, (await LifecycleAsync(fixture, T1, s => s.SubmitForReviewAsync(chain.RevisionId, stale, caller))).State);
        var approved = await SubmitAndApproveAsync(fixture, chain.RevisionId, draftStamp, T1, caller);
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, (await LifecycleAsync(fixture, T1.AddHours(1), s => s.IssueAsync(chain.RevisionId, draftStamp, caller))).State);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await LoadAsync(fixture, chain.RevisionId)).Status);

        Assert.True((await LifecycleAsync(fixture, T1.AddHours(1), s => s.IssueAsync(chain.RevisionId, approved, caller))).Succeeded);
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, (await LifecycleAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, stale, null, caller))).State);
        Assert.Single((await HistoryAsync(fixture, chain.ReportId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Database_Rejects_Post_Issuance_Lineage_Tampering_On_A_Real_Replacement()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var d1 = await SaveAsync(fixture, chain.RevisionId, "one", caller);
        var a1 = await SubmitAndApproveAsync(fixture, chain.RevisionId, d1, T1, caller);
        Assert.True((await LifecycleAsync(fixture, T1.AddHours(1), s => s.IssueAsync(chain.RevisionId, a1, caller))).Succeeded);
        var source = await LoadAsync(fixture, chain.RevisionId);
        var created = await LifecycleAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, source.ConcurrencyStamp, null, caller));
        var d2 = await SaveAsync(fixture, created.RevisionId!.Value, "two", caller);
        var a2 = await SubmitAndApproveAsync(fixture, created.RevisionId.Value, d2, T1.AddHours(3), caller);
        Assert.True((await LifecycleAsync(fixture, T1.AddHours(4), s => s.IssueAsync(created.RevisionId.Value, a2, caller))).Succeeded);

        await using var db = fixture.CreateDbContext();
        var replacementId = created.RevisionId.Value;
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""AssessmentReportRevisions"" SET ""BasedOnRevisionId"" = NULL WHERE ""Id"" = {replacementId}"));
        Assert.Equal("23514", (ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException)?.SqlState);
        Assert.Equal(chain.RevisionId, (await LoadAsync(fixture, replacementId)).BasedOnRevisionId);
    }
}
