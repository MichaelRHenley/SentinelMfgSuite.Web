using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using Xunit;
using Fixture = SentinelMfgSuite.Web.Tests.AssessmentReportRevisionContentServiceTests.Fixture;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// SQLite replacement-issuance tests. SQLite has no deferred supersession trigger, so these tests prove service
/// behavior only; PostgreSQL atomicity is covered by the guarded integration suite.
/// </summary>
public sealed class AssessmentReportReplacementIssuanceServiceTests
{
    private static readonly DateTimeOffset T1 = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubValidator(bool valid) : IAssessmentReportContentValidator
    {
        public AssessmentReportContentValidationResult Validate(
            SentinelMfgSuite.Web.Models.Assessments.Reports.ConsultantReportContent content,
            AssessmentReportContentValidationContext context)
            => new(valid
                ? Array.Empty<ReportContentValidationIssue>()
                : new[] { new ReportContentValidationIssue("x", ReportContentValidationSeverity.Error, "p", "m") });
    }

    private static AssessmentReportLifecycleService Create(Fixture f, bool valid = true)
        => new(f.Db, new StubValidator(valid), new ManualTimeProvider(T1));

    private static ClaimsPrincipal Caller(string id = "author-1")
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(ClaimTypes.Role, "AssessmentAdmin")
        }, "test"));

    private sealed record Pair(Fixture F, Guid PredecessorId, Guid ReplacementId);

    /// <summary>Revision 1 is Issued (predecessor); revision 2 is a same-report Approved replacement.</summary>
    private static async Task<Pair> CreatePairAsync(AssessmentReportRevisionStatus replacementStatus = AssessmentReportRevisionStatus.Approved, int predecessorNumber = 1, int replacementNumber = 2)
    {
        var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Issued);
        if (predecessorNumber != 1)
        {
            f.Revision.RevisionNumber = predecessorNumber;
            await f.Db.SaveChangesAsync();
        }
        var now = DateTime.UtcNow;
        var original = f.Revision;
        var replacement = new AssessmentReportRevision
        {
            Id = Guid.NewGuid(),
            AssessmentReportId = original.AssessmentReportId,
            RevisionNumber = replacementNumber,
            Status = replacementStatus,
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
            SubmittedForReviewUtc = now,
            SubmittedForReviewByUserId = "seed-submitter",
            ApprovedUtc = replacementStatus == AssessmentReportRevisionStatus.Approved ? now : null,
            ApprovedByUserId = replacementStatus == AssessmentReportRevisionStatus.Approved ? "seed-approver" : null,
            CreatedUtc = now,
            UpdatedUtc = now,
            CreatedByUserId = "seed",
            UpdatedByUserId = "seed"
        };
        f.Db.Set<AssessmentReportRevision>().Add(replacement);
        await f.Db.SaveChangesAsync();
        return new Pair(f, original.Id, replacement.Id);
    }

    private static async Task<AssessmentReportRevision> StoredAsync(Fixture f, Guid id)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options;
        await using var fresh = new ApplicationDbContext(options);
        return await fresh.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static async Task<List<AssessmentReportTransitionHistory>> HistoryAsync(Fixture f)
        => await f.Db.Set<AssessmentReportTransitionHistory>().AsNoTracking()
            .Where(h => h.AssessmentReportId == f.Report.Id).ToListAsync();

    [Fact]
    public async Task Replacement_Supersedes_Predecessor_Issues_Replacement_And_Pairs_History()
    {
        var p = await CreatePairAsync();
        await using var f = p.F;
        var before = await StoredAsync(f, p.PredecessorId);
        var replacementBefore = await StoredAsync(f, p.ReplacementId);

        var result = await Create(f).IssueAsync(p.ReplacementId, replacementBefore.ConcurrencyStamp, Caller());

        Assert.True(result.Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, result.Status);
        Assert.Equal(p.PredecessorId, result.SupersededRevisionId);
        Assert.NotNull(result.SupersededRevisionStamp);

        var predecessor = await StoredAsync(f, p.PredecessorId);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, predecessor.Status);
        Assert.Equal(p.ReplacementId, predecessor.SupersededByRevisionId);
        Assert.Equal(T1.UtcDateTime, predecessor.UpdatedUtc);
        Assert.Equal("author-1", predecessor.UpdatedByUserId);
        Assert.Equal(result.SupersededRevisionStamp, predecessor.ConcurrencyStamp);
        Assert.NotEqual(before.ConcurrencyStamp, predecessor.ConcurrencyStamp);
        Assert.Equal(before.SubmittedForReviewUtc, predecessor.SubmittedForReviewUtc);
        Assert.Equal(before.SubmittedForReviewByUserId, predecessor.SubmittedForReviewByUserId);
        Assert.Equal(before.ApprovedUtc, predecessor.ApprovedUtc);
        Assert.Equal(before.ApprovedByUserId, predecessor.ApprovedByUserId);
        Assert.Equal(before.IssuedUtc, predecessor.IssuedUtc);
        Assert.Equal(before.IssuedByUserId, predecessor.IssuedByUserId);
        Assert.Equal(before.ScoringSnapshot, predecessor.ScoringSnapshot);
        Assert.Equal(before.ConsultantContentJson, predecessor.ConsultantContentJson);
        Assert.Equal(before.PdfStorageKey, predecessor.PdfStorageKey);

        var replacement = await StoredAsync(f, p.ReplacementId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, replacement.Status);
        Assert.Equal(T1.UtcDateTime, replacement.IssuedUtc);
        Assert.Equal("author-1", replacement.IssuedByUserId);
        Assert.Equal(result.ConcurrencyStamp, replacement.ConcurrencyStamp);
        Assert.Null(replacement.SupersededByRevisionId);
        Assert.Equal(replacementBefore.ScoringSnapshot, replacement.ScoringSnapshot);
        Assert.Equal(replacementBefore.ConsultantContentJson, replacement.ConsultantContentJson);
        Assert.Equal(replacementBefore.ApprovedUtc, replacement.ApprovedUtc);
        Assert.Equal(replacementBefore.ApprovedByUserId, replacement.ApprovedByUserId);

        var history = await HistoryAsync(f);
        Assert.Equal(2, history.Count);
        var superseded = Assert.Single(history, h => h.AssessmentReportRevisionId == p.PredecessorId);
        var issued = Assert.Single(history, h => h.AssessmentReportRevisionId == p.ReplacementId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, superseded.PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, superseded.NewStatus);
        Assert.Equal(p.ReplacementId, superseded.ReplacementRevisionId);
        Assert.Null(superseded.ReplacedRevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, issued.PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, issued.NewStatus);
        Assert.Equal(p.PredecessorId, issued.ReplacedRevisionId);
        Assert.Null(issued.ReplacementRevisionId);
        Assert.All(history, h =>
        {
            Assert.Equal(AssessmentReportTransitionEventType.Transition, h.EventType);
            Assert.Equal(f.Report.Id, h.AssessmentReportId);
            Assert.Equal("author-1", h.ActorUserId);
            Assert.Equal(T1.UtcDateTime, h.OccurredUtc);
            Assert.Null(h.Reason);
        });
        Assert.False(string.IsNullOrWhiteSpace(superseded.CorrelationId));
        Assert.Equal(superseded.CorrelationId, issued.CorrelationId);
    }

    [Fact]
    public async Task Replacement_Must_Have_Greater_Revision_Number()
    {
        var p = await CreatePairAsync(predecessorNumber: 3, replacementNumber: 2);
        await using var f = p.F;
        var stamp = (await StoredAsync(f, p.ReplacementId)).ConcurrencyStamp;

        var result = await Create(f).IssueAsync(p.ReplacementId, stamp, Caller());

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, p.PredecessorId)).Status);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, p.ReplacementId)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Theory]
    [InlineData(AssessmentReportRevisionStatus.Rejected)]
    [InlineData(AssessmentReportRevisionStatus.InReview)]
    public async Task Non_Approved_Replacement_Is_Rejected_Without_Changes(AssessmentReportRevisionStatus status)
    {
        var p = await CreatePairAsync(status);
        await using var f = p.F;
        var stored = await StoredAsync(f, p.ReplacementId);

        var result = await Create(f).IssueAsync(p.ReplacementId, stored.ConcurrencyStamp, Caller());

        Assert.False(result.Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, p.PredecessorId)).Status);
        Assert.Equal(status, (await StoredAsync(f, p.ReplacementId)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Stale_Replacement_Stamp_Returns_ConcurrencyConflict_And_Changes_Nothing()
    {
        var p = await CreatePairAsync();
        await using var f = p.F;

        var result = await Create(f).IssueAsync(p.ReplacementId, Guid.NewGuid(), Caller());

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, p.PredecessorId)).Status);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, p.ReplacementId)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Invalid_Source_Data_And_Validation_Failure_Do_Not_Supersede()
    {
        var p = await CreatePairAsync();
        await using var f = p.F;
        var stamp = (await StoredAsync(f, p.ReplacementId)).ConcurrencyStamp;

        var invalid = await Create(f, valid: false).IssueAsync(p.ReplacementId, stamp, Caller());
        Assert.Equal(AssessmentReportLifecycleState.ValidationFailed, invalid.State);

        var tracked = await f.Db.Set<AssessmentReportRevision>().SingleAsync(r => r.Id == p.ReplacementId);
        tracked.ScoringSnapshot = null;
        await f.Db.SaveChangesAsync();
        var newStamp = (await StoredAsync(f, p.ReplacementId)).ConcurrencyStamp;
        var missing = await Create(f).IssueAsync(p.ReplacementId, newStamp, Caller());
        Assert.Equal(AssessmentReportLifecycleState.InvalidSourceData, missing.State);

        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, p.PredecessorId)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Duplicate_Replacement_Issuance_Does_Not_Create_Duplicate_History()
    {
        var p = await CreatePairAsync();
        await using var f = p.F;
        var service = Create(f);
        var stamp = (await StoredAsync(f, p.ReplacementId)).ConcurrencyStamp;

        var first = await service.IssueAsync(p.ReplacementId, stamp, Caller());
        var stale = await service.IssueAsync(p.ReplacementId, stamp, Caller());
        var current = await service.IssueAsync(p.ReplacementId, first.ConcurrencyStamp!.Value, Caller());

        Assert.True(first.Succeeded);
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, stale.State);
        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, current.State);
        Assert.Equal(2, (await HistoryAsync(f)).Count);
    }

    [Fact]
    public async Task Replacement_Clock_Earlier_Than_Predecessor_Issuance_Is_Rejected_Without_Writes()
    {
        var p = await CreatePairAsync();
        await using var f = p.F;
        var predecessorBefore = await StoredAsync(f, p.PredecessorId);
        var replacementBefore = await StoredAsync(f, p.ReplacementId);
        var earlier = new AssessmentReportLifecycleService(f.Db, new StubValidator(true),
            new ManualTimeProvider(new DateTimeOffset(predecessorBefore.IssuedUtc!.Value.AddSeconds(-1), TimeSpan.Zero)));

        var result = await earlier.IssueAsync(p.ReplacementId, replacementBefore.ConcurrencyStamp, Caller());

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        var predecessor = await StoredAsync(f, p.PredecessorId);
        var replacement = await StoredAsync(f, p.ReplacementId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, predecessor.Status);
        Assert.Equal(predecessorBefore.ConcurrencyStamp, predecessor.ConcurrencyStamp);
        Assert.Equal(predecessorBefore.UpdatedUtc, predecessor.UpdatedUtc);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, replacement.Status);
        Assert.Equal(replacementBefore.ConcurrencyStamp, replacement.ConcurrencyStamp);
        Assert.Null(replacement.IssuedUtc);
        Assert.Empty(await HistoryAsync(f));
    }

    private sealed class FirstSaveHook(Func<DbContext, Task> onFirstSave) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private int _fired;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await onFirstSave(eventData.Context!);
            }
            return result;
        }
    }

    [Fact]
    public async Task Stale_Predecessor_Stamp_At_Save_Returns_ConcurrencyConflict_And_Persists_Nothing()
    {
        var p = await CreatePairAsync();
        await using var f = p.F;
        var replacementStamp = (await StoredAsync(f, p.ReplacementId)).ConcurrencyStamp;
        var changedStamp = Guid.NewGuid();
        var predecessorStampBefore = (await StoredAsync(f, p.PredecessorId)).ConcurrencyStamp;
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(f.Db.Database.GetConnectionString()!)
            .AddInterceptors(new FirstSaveHook(async context =>
            {
                // Runs after the service read the predecessor and before its first save. SQLite allows one writer, so the
                // competing change goes through the service's own connection (a bulk update that bypasses the tracker).
                await context.Set<AssessmentReportRevision>().Where(r => r.Id == p.PredecessorId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConcurrencyStamp, changedStamp));
            }))
            .Options;
        await using var hooked = new ApplicationDbContext(options);
        var service = new AssessmentReportLifecycleService(hooked, new StubValidator(true), new ManualTimeProvider(T1));

        var result = await service.IssueAsync(p.ReplacementId, replacementStamp, Caller());

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        var predecessor = await StoredAsync(f, p.PredecessorId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, predecessor.Status);
        Assert.Null(predecessor.SupersededByRevisionId);
        Assert.Equal(predecessorStampBefore, predecessor.ConcurrencyStamp);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, p.ReplacementId)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Replacement_Stamp_Changed_Before_Load_Returns_ConcurrencyConflict_And_Changes_Nothing()
    {
        var p = await CreatePairAsync();
        await using var f = p.F;
        var replacementStamp = (await StoredAsync(f, p.ReplacementId)).ConcurrencyStamp;
        var service = Create(f);
        var changedStamp = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options;

        // Changed after the caller read the stamp but before the service loads the revision, so the conflict is the
        // up-front stamp check; the save-time paths are covered by the interceptor-based tests.
        await using (var other = new ApplicationDbContext(options))
        {
            await other.Set<AssessmentReportRevision>().Where(r => r.Id == p.ReplacementId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConcurrencyStamp, changedStamp));
        }

        var result = await service.IssueAsync(p.ReplacementId, replacementStamp, Caller());

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, p.PredecessorId)).Status);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, p.ReplacementId)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task History_Insert_Failure_Rolls_Back_Both_Revision_Updates()
    {
        var p = await CreatePairAsync();
        await using var f = p.F;
        var table = f.Db.Model.FindEntityType(typeof(AssessmentReportTransitionHistory))!.GetTableName();
#pragma warning disable EF1002
        await f.Db.Database.ExecuteSqlRawAsync(
            $"CREATE TRIGGER fail_history BEFORE INSERT ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'blocked'); END;");
#pragma warning restore EF1002
        var before = await StoredAsync(f, p.PredecessorId);
        var stamp = (await StoredAsync(f, p.ReplacementId)).ConcurrencyStamp;

        await Assert.ThrowsAsync<DbUpdateException>(() => Create(f).IssueAsync(p.ReplacementId, stamp, Caller()));

        var predecessor = await StoredAsync(f, p.PredecessorId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, predecessor.Status);
        Assert.Null(predecessor.SupersededByRevisionId);
        Assert.Equal(before.ConcurrencyStamp, predecessor.ConcurrencyStamp);
        var replacement = await StoredAsync(f, p.ReplacementId);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, replacement.Status);
        Assert.Null(replacement.IssuedUtc);
        Assert.Equal(stamp, replacement.ConcurrencyStamp);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Initial_Issuance_Still_Works_When_No_Other_Issued_Revision_Exists()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Approved);

        var result = await Create(f).IssueAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller());

        Assert.True(result.Succeeded);
        Assert.Null(result.SupersededRevisionId);
        Assert.Null(result.SupersededRevisionStamp);
        var history = Assert.Single(await HistoryAsync(f));
        Assert.Null(history.ReplacedRevisionId);
    }
}
