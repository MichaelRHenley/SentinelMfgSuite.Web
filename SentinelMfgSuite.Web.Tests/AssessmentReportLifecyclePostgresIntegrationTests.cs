using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Lifecycle persistence tests against the guarded PostgreSQL test database. Transition history is
/// append-only, so chains created here are intentionally left in place (see the report integration cleanup).
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
public sealed class AssessmentReportLifecyclePostgresIntegrationTests
{
    private static readonly DateTimeOffset T1 = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(1);
    private static readonly DateTimeOffset T3 = T1.AddHours(2);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PassingValidator : IAssessmentReportContentValidator
    {
        public AssessmentReportContentValidationResult Validate(
            ConsultantReportContent content, AssessmentReportContentValidationContext context)
            => new(Array.Empty<ReportContentValidationIssue>());
    }

    private static async Task<AssessmentReportLifecycleResult> RunAsync(
        PostgresIntegrationTestFixture fixture,
        TimeProvider time,
        Func<AssessmentReportLifecycleService, Task<AssessmentReportLifecycleResult>> action)
    {
        await using var db = fixture.CreateDbContext();
        return await action(new AssessmentReportLifecycleService(db, new PassingValidator(), time));
    }

    private static async Task<AssessmentReportRevision> LoadRevisionAsync(PostgresIntegrationTestFixture fixture, Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static async Task<List<AssessmentReportTransitionHistory>> HistoryAsync(PostgresIntegrationTestFixture fixture, Guid revisionId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<AssessmentReportTransitionHistory>().AsNoTracking()
            .Where(h => h.AssessmentReportRevisionId == revisionId && h.EventType == AssessmentReportTransitionEventType.Transition)
            .OrderBy(h => h.OccurredUtc).ThenBy(h => h.NewStatus).ToListAsync();
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Submit_Approve_Persist_Stamps_And_Single_History_Event_Each()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var original = await LoadRevisionAsync(fixture, chain.RevisionId);

        var submit = await RunAsync(fixture, time, s => s.SubmitForReviewAsync(chain.RevisionId, original.ConcurrencyStamp, caller));
        time.Now = T2;
        var approve = await RunAsync(fixture, time, s => s.ApproveAsync(chain.RevisionId, submit.ConcurrencyStamp!.Value, caller));

        Assert.True(submit.Succeeded);
        Assert.True(approve.Succeeded);
        var stored = await LoadRevisionAsync(fixture, chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, stored.Status);
        Assert.Equal(T1.UtcDateTime, stored.SubmittedForReviewUtc);
        Assert.Equal(chain.UserId, stored.SubmittedForReviewByUserId);
        Assert.Equal(T2.UtcDateTime, stored.ApprovedUtc);
        Assert.Equal(chain.UserId, stored.ApprovedByUserId);
        Assert.Equal(T2.UtcDateTime, stored.UpdatedUtc);
        Assert.Equal(original.ScoringSnapshot, stored.ScoringSnapshot);
        Assert.Equal(original.ConsultantContentJson, stored.ConsultantContentJson);

        var history = await HistoryAsync(fixture, chain.RevisionId);
        Assert.Equal(2, history.Count);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, history[0].PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.InReview, history[0].NewStatus);
        Assert.Equal(AssessmentReportRevisionStatus.InReview, history[1].PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, history[1].NewStatus);
        Assert.All(history, h =>
        {
            Assert.Equal(chain.UserId, h.ActorUserId);
            Assert.Null(h.Reason);
            Assert.Null(h.ReplacedRevisionId);
            Assert.Null(h.ReplacementRevisionId);
            Assert.False(string.IsNullOrWhiteSpace(h.CorrelationId));
        });
        Assert.Equal(T1.UtcDateTime, history[0].OccurredUtc);
        Assert.Equal(T2.UtcDateTime, history[1].OccurredUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgresIntegration")]
    public async Task Reject_From_InReview_Or_Approved_Stores_Normalized_Reason_And_Preserves_Stamps(bool approveFirst)
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var stamp = (await LoadRevisionAsync(fixture, chain.RevisionId)).ConcurrencyStamp;
        stamp = (await RunAsync(fixture, time, s => s.SubmitForReviewAsync(chain.RevisionId, stamp, caller))).ConcurrencyStamp!.Value;
        if (approveFirst)
        {
            time.Now = T2;
            var approvalStamp = stamp;
            stamp = (await RunAsync(fixture, time, s => s.ApproveAsync(chain.RevisionId, approvalStamp, caller))).ConcurrencyStamp!.Value;
        }
        time.Now = T3;

        var rejected = await RunAsync(fixture, time, s => s.RejectAsync(chain.RevisionId, stamp, "  Not ready  ", caller));

        Assert.True(rejected.Succeeded);
        var stored = await LoadRevisionAsync(fixture, chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Rejected, stored.Status);
        Assert.Equal(T1.UtcDateTime, stored.SubmittedForReviewUtc);
        Assert.Equal(approveFirst ? T2.UtcDateTime : null, stored.ApprovedUtc);
        var last = (await HistoryAsync(fixture, chain.RevisionId)).Last();
        Assert.Equal(AssessmentReportRevisionStatus.Rejected, last.NewStatus);
        Assert.Equal(approveFirst ? AssessmentReportRevisionStatus.Approved : AssessmentReportRevisionStatus.InReview, last.PreviousStatus);
        Assert.Equal("Not ready", last.Reason);
        Assert.Equal(T3.UtcDateTime, last.OccurredUtc);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Invalid_Reason_Invalid_Transition_And_Stale_Stamps_Persist_Nothing()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var draftStamp = (await LoadRevisionAsync(fixture, chain.RevisionId)).ConcurrencyStamp;

        var approveDraft = await RunAsync(fixture, time, s => s.ApproveAsync(chain.RevisionId, draftStamp, caller));
        var rejectDraft = await RunAsync(fixture, time, s => s.RejectAsync(chain.RevisionId, draftStamp, "reason", caller));
        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, approveDraft.State);
        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, rejectDraft.State);
        Assert.Empty(await HistoryAsync(fixture, chain.RevisionId));

        var submit = await RunAsync(fixture, time, s => s.SubmitForReviewAsync(chain.RevisionId, draftStamp, caller));
        var staleSubmit = await RunAsync(fixture, time, s => s.SubmitForReviewAsync(chain.RevisionId, draftStamp, caller));
        var blankReason = await RunAsync(fixture, time, s => s.RejectAsync(chain.RevisionId, submit.ConcurrencyStamp!.Value, "   ", caller));
        var tooLong = await RunAsync(fixture, time, s => s.RejectAsync(chain.RevisionId, submit.ConcurrencyStamp!.Value,
            new string('x', AssessmentReportTransitionPolicy.MaxReasonLength + 1), caller));

        Assert.True(submit.Succeeded);
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, staleSubmit.State);
        Assert.Equal(AssessmentReportLifecycleState.InvalidReason, blankReason.State);
        Assert.Equal(AssessmentReportLifecycleState.InvalidReason, tooLong.State);
        var stored = await LoadRevisionAsync(fixture, chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.InReview, stored.Status);
        Assert.Equal(submit.ConcurrencyStamp, stored.ConcurrencyStamp);
        Assert.Single(await HistoryAsync(fixture, chain.RevisionId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Competing_Transitions_Allow_Exactly_One_Winner_And_No_Duplicate_History()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var stamp = (await LoadRevisionAsync(fixture, chain.RevisionId)).ConcurrencyStamp;

        var submits = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => RunAsync(fixture, time, s => s.SubmitForReviewAsync(chain.RevisionId, stamp, caller)))));
        Assert.Equal(1, submits.Count(r => r.Succeeded));
        Assert.Equal(3, submits.Count(r => r.State == AssessmentReportLifecycleState.ConcurrencyConflict));
        var inReviewStamp = submits.Single(r => r.Succeeded).ConcurrencyStamp!.Value;

        var competing = await Task.WhenAll(
            Task.Run(() => RunAsync(fixture, time, s => s.ApproveAsync(chain.RevisionId, inReviewStamp, caller))),
            Task.Run(() => RunAsync(fixture, time, s => s.RejectAsync(chain.RevisionId, inReviewStamp, "competing", caller))));

        Assert.Equal(1, competing.Count(r => r.Succeeded));
        Assert.Equal(1, competing.Count(r => r.State == AssessmentReportLifecycleState.ConcurrencyConflict));
        var history = await HistoryAsync(fixture, chain.RevisionId);
        Assert.Equal(2, history.Count);
        var stored = await LoadRevisionAsync(fixture, chain.RevisionId);
        Assert.Equal(history.Last().NewStatus, stored.Status);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Simultaneous_Approvals_Allow_Exactly_One_Winner_And_Single_History_Event()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var stamp = (await LoadRevisionAsync(fixture, chain.RevisionId)).ConcurrencyStamp;
        var submit = await RunAsync(fixture, time, s => s.SubmitForReviewAsync(chain.RevisionId, stamp, caller));
        var inReviewStamp = submit.ConcurrencyStamp!.Value;

        var approvals = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Task.Run(() => RunAsync(fixture, time, s => s.ApproveAsync(chain.RevisionId, inReviewStamp, caller)))));

        Assert.Equal(1, approvals.Count(r => r.Succeeded));
        Assert.Equal(1, approvals.Count(r => r.State == AssessmentReportLifecycleState.ConcurrencyConflict));
        var history = await HistoryAsync(fixture, chain.RevisionId);
        Assert.Equal(2, history.Count);
        Assert.Equal(1, history.Count(h => h.NewStatus == AssessmentReportRevisionStatus.Approved));
    }

    private static async Task<Guid> ApproveAsync(PostgresIntegrationTestFixture fixture, Guid revisionId, ClaimsPrincipal caller, TimeProvider time)
    {
        var stamp = (await LoadRevisionAsync(fixture, revisionId)).ConcurrencyStamp;
        var submit = await RunAsync(fixture, time, s => s.SubmitForReviewAsync(revisionId, stamp, caller));
        Assert.True(submit.Succeeded);
        var approve = await RunAsync(fixture, time, s => s.ApproveAsync(revisionId, submit.ConcurrencyStamp!.Value, caller));
        Assert.True(approve.Succeeded);
        return approve.ConcurrencyStamp!.Value;
    }

    private static async Task<Guid> CloneDraftRevisionAsync(PostgresIntegrationTestFixture fixture, AssessmentReportRevisionSource source, int number)
    {
        await using var db = fixture.CreateDbContext();
        var original = await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == source.RevisionId);
        var now = DateTime.UtcNow;
        var clone = new AssessmentReportRevision
        {
            Id = Guid.NewGuid(),
            AssessmentReportId = original.AssessmentReportId,
            RevisionNumber = number,
            ExecutiveSummary = original.ExecutiveSummary,
            ScopeAndMethodology = original.ScopeAndMethodology,
            AssumptionsAndLimitations = original.AssumptionsAndLimitations,
            NextSteps = original.NextSteps,
            ScoringContractVersion = original.ScoringContractVersion,
            ScoringSnapshotSchemaVersion = original.ScoringSnapshotSchemaVersion,
            ScoringSnapshot = original.ScoringSnapshot,
            ConsultantContentJson = original.ConsultantContentJson,
            ConsultantContentSchemaVersion = original.ConsultantContentSchemaVersion,
            ReportTemplateVersion = original.ReportTemplateVersion,
            CreatedUtc = now,
            UpdatedUtc = now,
            CreatedByUserId = original.CreatedByUserId,
            UpdatedByUserId = original.UpdatedByUserId
        };
        db.Set<AssessmentReportRevision>().Add(clone);
        await db.SaveChangesAsync();
        return clone.Id;
    }

    private sealed record AssessmentReportRevisionSource(Guid RevisionId);

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Initial_Issuance_Persists_Stamps_Preserves_Frozen_Data_And_Writes_One_History_Event()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var approvedStamp = await ApproveAsync(fixture, chain.RevisionId, caller, time);
        var approved = await LoadRevisionAsync(fixture, chain.RevisionId);
        time.Now = T3;

        var issued = await RunAsync(fixture, time, s => s.IssueAsync(chain.RevisionId, approvedStamp, caller));

        Assert.True(issued.Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, issued.Status);
        var stored = await LoadRevisionAsync(fixture, chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, stored.Status);
        Assert.Equal(T3.UtcDateTime, stored.IssuedUtc);
        Assert.Equal(chain.UserId, stored.IssuedByUserId);
        Assert.Equal(T3.UtcDateTime, stored.UpdatedUtc);
        Assert.Equal(issued.ConcurrencyStamp, stored.ConcurrencyStamp);
        Assert.Null(stored.SupersededByRevisionId);
        Assert.Equal(approved.ScoringSnapshot, stored.ScoringSnapshot);
        Assert.Equal(approved.ConsultantContentJson, stored.ConsultantContentJson);
        Assert.Equal(approved.SubmittedForReviewUtc, stored.SubmittedForReviewUtc);
        Assert.Equal(approved.SubmittedForReviewByUserId, stored.SubmittedForReviewByUserId);
        Assert.Equal(approved.ApprovedUtc, stored.ApprovedUtc);
        Assert.Equal(approved.ApprovedByUserId, stored.ApprovedByUserId);
        Assert.Null(stored.PdfStorageKey);

        var history = (await HistoryAsync(fixture, chain.RevisionId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued).ToList();
        var evt = Assert.Single(history);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, evt.PreviousStatus);
        Assert.Equal(chain.UserId, evt.ActorUserId);
        Assert.Equal(T3.UtcDateTime, evt.OccurredUtc);
        Assert.Null(evt.Reason);
        Assert.Null(evt.ReplacedRevisionId);
        Assert.Null(evt.ReplacementRevisionId);
        Assert.False(string.IsNullOrWhiteSpace(evt.CorrelationId));
        Assert.Equal(3, (await HistoryAsync(fixture, chain.RevisionId)).Count);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Simultaneous_Issuance_Of_Same_Revision_Allows_One_Winner_And_Single_History_Event()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var stamp = await ApproveAsync(fixture, chain.RevisionId, caller, time);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => RunAsync(fixture, time, s => s.IssueAsync(chain.RevisionId, stamp, caller)))));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(3, results.Count(r => r.State == AssessmentReportLifecycleState.ConcurrencyConflict));
        Assert.Equal(1, (await HistoryAsync(fixture, chain.RevisionId)).Count(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await LoadRevisionAsync(fixture, chain.RevisionId)).Status);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Competing_Issuance_Of_Different_Approved_Revisions_Allows_Exactly_One_And_Leaves_Loser_Approved()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var secondId = await CloneDraftRevisionAsync(fixture, new AssessmentReportRevisionSource(chain.RevisionId), 2);
        var firstStamp = await ApproveAsync(fixture, chain.RevisionId, caller, time);
        var secondStamp = await ApproveAsync(fixture, secondId, caller, time);

        var results = await Task.WhenAll(
            Task.Run(() => RunAsync(fixture, time, s => s.IssueAsync(chain.RevisionId, firstStamp, caller))),
            Task.Run(() => RunAsync(fixture, time, s => s.IssueAsync(secondId, secondStamp, caller))));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => r.State == AssessmentReportLifecycleState.InvalidTransition));
        var first = await LoadRevisionAsync(fixture, chain.RevisionId);
        var second = await LoadRevisionAsync(fixture, secondId);
        Assert.Single(new[] { first, second }, r => r.Status == AssessmentReportRevisionStatus.Issued);
        var loser = first.Status == AssessmentReportRevisionStatus.Issued ? second : first;
        Assert.Equal(AssessmentReportRevisionStatus.Approved, loser.Status);
        Assert.Null(loser.IssuedUtc);
        Assert.Empty((await HistoryAsync(fixture, loser.Id)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
        Assert.Single((await HistoryAsync(fixture, loser.Id == first.Id ? second.Id : first.Id)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Issuing_While_Another_Revision_Is_Issued_Returns_InvalidTransition_And_Unique_Index_Rejects_Direct_Update()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var secondId = await CloneDraftRevisionAsync(fixture, new AssessmentReportRevisionSource(chain.RevisionId), 2);
        var firstStamp = await ApproveAsync(fixture, chain.RevisionId, caller, time);
        var secondStamp = await ApproveAsync(fixture, secondId, caller, time);
        Assert.True((await RunAsync(fixture, time, s => s.IssueAsync(chain.RevisionId, firstStamp, caller))).Succeeded);

        var blocked = await RunAsync(fixture, time, s => s.IssueAsync(secondId, secondStamp, caller));

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, blocked.State);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await LoadRevisionAsync(fixture, secondId)).Status);

        await using var db = fixture.CreateDbContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 3, ""IssuedUtc"" = {DateTime.UtcNow}, ""IssuedByUserId"" = {"direct"} WHERE ""Id"" = {secondId}"));
        var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
        Assert.NotNull(pg);
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, pg!.SqlState);
        Assert.Equal("UX_AssessmentReportRevisions_Report_CurrentIssued", pg.ConstraintName);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Failed_History_Insert_Rolls_Back_Issuance()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var stamp = await ApproveAsync(fixture, chain.RevisionId, caller, time);
        var before = await LoadRevisionAsync(fixture, chain.RevisionId);

        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportTransitionHistory""
(""Id"", ""AssessmentReportId"", ""AssessmentReportRevisionId"", ""EventType"", ""PreviousStatus"", ""NewStatus"", ""ActorUserId"", ""OccurredUtc"", ""Reason"", ""ReplacedRevisionId"", ""ReplacementRevisionId"", ""CorrelationId"")
VALUES ({Guid.NewGuid()}, {chain.ReportId}, {chain.RevisionId}, {(short)2}, {(short)2}, {(short)3}, {"seed-actor"}, {DateTime.UtcNow}, {(string?)null}, {(Guid?)null}, {(Guid?)null}, {"seed-correlation"})");
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(fixture, time, s => s.IssueAsync(chain.RevisionId, stamp, caller)));

        var after = await LoadRevisionAsync(fixture, chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, after.Status);
        Assert.Null(after.IssuedUtc);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Equal("seed-actor", Assert.Single((await HistoryAsync(fixture, chain.RevisionId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued)).ActorUserId);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Failed_History_Insert_Rolls_Back_Revision_Update()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var before = await LoadRevisionAsync(fixture, chain.RevisionId);

        // Insert-only seed of a conflicting Draft -> InReview event so the service's history insert
        // violates UX_AssessmentReportTransitionHistory_Revision_NewStatus.
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportTransitionHistory""
(""Id"", ""AssessmentReportId"", ""AssessmentReportRevisionId"", ""EventType"", ""PreviousStatus"", ""NewStatus"", ""ActorUserId"", ""OccurredUtc"", ""Reason"", ""ReplacedRevisionId"", ""ReplacementRevisionId"", ""CorrelationId"")
VALUES ({Guid.NewGuid()}, {chain.ReportId}, {chain.RevisionId}, {(short)2}, {(short)0}, {(short)1}, {"seed-actor"}, {DateTime.UtcNow}, {(string?)null}, {(Guid?)null}, {(Guid?)null}, {"seed-correlation"})");
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(fixture, new ManualTimeProvider(T1),
            s => s.SubmitForReviewAsync(chain.RevisionId, before.ConcurrencyStamp, caller)));

        var after = await LoadRevisionAsync(fixture, chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, after.Status);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Null(after.SubmittedForReviewUtc);
        Assert.Null(after.SubmittedForReviewByUserId);
        var history = await HistoryAsync(fixture, chain.RevisionId);
        Assert.Equal("seed-actor", Assert.Single(history).ActorUserId);
    }
}