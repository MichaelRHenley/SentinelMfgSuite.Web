using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>Releases all participants only after every one has arrived (explicit race synchronization).</summary>
    private sealed class Rendezvous(int participants)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == participants)
            {
                _all.TrySetResult();
            }
            return _all.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Runs a callback once, immediately before the first SaveChanges of the context that uses it.</summary>
    private sealed class FirstSaveHook(Func<Task> onFirstSave) : SaveChangesInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await onFirstSave();
            }
            return result;
        }
    }

    private static async Task<AssessmentReportLifecycleResult> RunHookedAsync(
        PostgresIntegrationTestFixture fixture,
        TimeProvider time,
        Func<Task> onFirstSave,
        Func<AssessmentReportLifecycleService, Task<AssessmentReportLifecycleResult>> action)
    {
        var options = new DbContextOptionsBuilder<SentinelMfgSuite.Web.Data.ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new FirstSaveHook(onFirstSave))
            .Options;
        await using var db = new SentinelMfgSuite.Web.Data.ApplicationDbContext(options);
        return await action(new AssessmentReportLifecycleService(db, new PassingValidator(), time));
    }

    private sealed record IssuedPair(Guid PredecessorId, Guid ReplacementId, Guid ReplacementStamp);

    /// <summary>Predecessor (revision 1) Issued at <paramref name="issuedAt"/>; replacement (revision 2) Approved.</summary>
    private static async Task<IssuedPair> CreateIssuedPairAsync(
        PostgresIntegrationTestFixture fixture, AssessmentReportPostgresIntegrationTests.ReportChain chain, ClaimsPrincipal caller, DateTimeOffset issuedAt)
    {
        var approveTime = new ManualTimeProvider(T1);
        var replacementId = await CloneDraftRevisionAsync(fixture, new AssessmentReportRevisionSource(chain.RevisionId), 2);
        var predecessorStamp = await ApproveAsync(fixture, chain.RevisionId, caller, approveTime);
        var replacementStamp = await ApproveAsync(fixture, replacementId, caller, approveTime);
        var issued = await RunAsync(fixture, new ManualTimeProvider(issuedAt), s => s.IssueAsync(chain.RevisionId, predecessorStamp, caller));
        Assert.True(issued.Succeeded);
        return new IssuedPair(chain.RevisionId, replacementId, replacementStamp);
    }

    private static async Task<int> IssuedCountAsync(PostgresIntegrationTestFixture fixture, Guid reportId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<AssessmentReportRevision>().CountAsync(r => r.AssessmentReportId == reportId && r.Status == AssessmentReportRevisionStatus.Issued);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Replacement_Issuance_Is_Atomic_Paired_And_Preserves_Historical_Data()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var pair = await CreateIssuedPairAsync(fixture, chain, caller, T2);
        var predecessorBefore = await LoadRevisionAsync(fixture, pair.PredecessorId);
        var replacementBefore = await LoadRevisionAsync(fixture, pair.ReplacementId);

        var result = await RunAsync(fixture, new ManualTimeProvider(T3), s => s.IssueAsync(pair.ReplacementId, pair.ReplacementStamp, caller));

        Assert.True(result.Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, result.Status);
        Assert.Equal(pair.PredecessorId, result.SupersededRevisionId);
        var predecessor = await LoadRevisionAsync(fixture, pair.PredecessorId);
        var replacement = await LoadRevisionAsync(fixture, pair.ReplacementId);
        Assert.Equal(result.ConcurrencyStamp, replacement.ConcurrencyStamp);
        Assert.Equal(result.SupersededRevisionStamp, predecessor.ConcurrencyStamp);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, predecessor.Status);
        Assert.Equal(pair.ReplacementId, predecessor.SupersededByRevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, replacement.Status);
        Assert.Equal(T3.UtcDateTime, replacement.IssuedUtc);
        Assert.Equal(chain.UserId, replacement.IssuedByUserId);
        Assert.Null(replacement.SupersededByRevisionId);
        Assert.Equal(1, await IssuedCountAsync(fixture, chain.ReportId));

        // Historical audit stamps and frozen data are untouched.
        Assert.Equal(predecessorBefore.SubmittedForReviewUtc, predecessor.SubmittedForReviewUtc);
        Assert.Equal(predecessorBefore.SubmittedForReviewByUserId, predecessor.SubmittedForReviewByUserId);
        Assert.Equal(predecessorBefore.ApprovedUtc, predecessor.ApprovedUtc);
        Assert.Equal(predecessorBefore.ApprovedByUserId, predecessor.ApprovedByUserId);
        Assert.Equal(predecessorBefore.IssuedUtc, predecessor.IssuedUtc);
        Assert.Equal(predecessorBefore.IssuedByUserId, predecessor.IssuedByUserId);
        Assert.Equal(T3.UtcDateTime, predecessor.UpdatedUtc);
        Assert.Equal(predecessorBefore.ScoringSnapshot, predecessor.ScoringSnapshot);
        Assert.Equal(predecessorBefore.ConsultantContentJson, predecessor.ConsultantContentJson);
        Assert.Equal(replacementBefore.ScoringSnapshot, replacement.ScoringSnapshot);
        Assert.Equal(replacementBefore.ConsultantContentJson, replacement.ConsultantContentJson);
        Assert.Equal(replacementBefore.ApprovedUtc, replacement.ApprovedUtc);
        Assert.Null(replacement.PdfStorageKey);
        Assert.Null(predecessor.PdfStorageKey);

        var superseded = Assert.Single((await HistoryAsync(fixture, pair.PredecessorId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Superseded));
        var issued = Assert.Single((await HistoryAsync(fixture, pair.ReplacementId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
        Assert.Equal(AssessmentReportRevisionStatus.Issued, superseded.PreviousStatus);
        Assert.Equal(pair.ReplacementId, superseded.ReplacementRevisionId);
        Assert.Null(superseded.ReplacedRevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, issued.PreviousStatus);
        Assert.Equal(pair.PredecessorId, issued.ReplacedRevisionId);
        Assert.Null(issued.ReplacementRevisionId);
        Assert.Equal(chain.ReportId, superseded.AssessmentReportId);
        Assert.Equal(chain.ReportId, issued.AssessmentReportId);
        Assert.Equal(chain.UserId, superseded.ActorUserId);
        Assert.Equal(chain.UserId, issued.ActorUserId);
        Assert.Equal(T3.UtcDateTime, superseded.OccurredUtc);
        Assert.Equal(T3.UtcDateTime, issued.OccurredUtc);
        Assert.False(string.IsNullOrWhiteSpace(superseded.CorrelationId));
        Assert.Equal(superseded.CorrelationId, issued.CorrelationId);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Stale_Replacement_Stamp_Returns_ConcurrencyConflict_And_Changes_Nothing()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var pair = await CreateIssuedPairAsync(fixture, chain, caller, T2);
        var predecessorBefore = await LoadRevisionAsync(fixture, pair.PredecessorId);

        var result = await RunAsync(fixture, new ManualTimeProvider(T3), s => s.IssueAsync(pair.ReplacementId, Guid.NewGuid(), caller));

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        var predecessor = await LoadRevisionAsync(fixture, pair.PredecessorId);
        var replacement = await LoadRevisionAsync(fixture, pair.ReplacementId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, predecessor.Status);
        Assert.Equal(predecessorBefore.ConcurrencyStamp, predecessor.ConcurrencyStamp);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, replacement.Status);
        Assert.Equal(pair.ReplacementStamp, replacement.ConcurrencyStamp);
        Assert.Empty((await HistoryAsync(fixture, pair.PredecessorId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Superseded));
        Assert.Empty((await HistoryAsync(fixture, pair.ReplacementId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Two_Competing_Attempts_To_Issue_The_Same_Replacement_Allow_Exactly_One()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var pair = await CreateIssuedPairAsync(fixture, chain, caller, T2);
        var gate = new Rendezvous(2);

        // Both operations have read the predecessor and the replacement before either saves.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            RunHookedAsync(fixture, new ManualTimeProvider(T3), gate.ArriveAsync,
                s => s.IssueAsync(pair.ReplacementId, pair.ReplacementStamp, caller)))));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => r.State == AssessmentReportLifecycleState.ConcurrencyConflict));
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, (await LoadRevisionAsync(fixture, pair.PredecessorId)).Status);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await LoadRevisionAsync(fixture, pair.ReplacementId)).Status);
        Assert.Equal(1, await IssuedCountAsync(fixture, chain.ReportId));
        Assert.Single((await HistoryAsync(fixture, pair.PredecessorId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Superseded));
        Assert.Single((await HistoryAsync(fixture, pair.ReplacementId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Two_Different_Approved_Revisions_Competing_For_The_Same_Predecessor_Allow_Exactly_One()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var pair = await CreateIssuedPairAsync(fixture, chain, caller, T2);
        var thirdId = await CloneDraftRevisionAsync(fixture, new AssessmentReportRevisionSource(chain.RevisionId), 3);
        var thirdStamp = await ApproveAsync(fixture, thirdId, caller, new ManualTimeProvider(T1));
        var gate = new Rendezvous(2);

        var results = await Task.WhenAll(
            Task.Run(() => RunHookedAsync(fixture, new ManualTimeProvider(T3), gate.ArriveAsync, s => s.IssueAsync(pair.ReplacementId, pair.ReplacementStamp, caller))),
            Task.Run(() => RunHookedAsync(fixture, new ManualTimeProvider(T3), gate.ArriveAsync, s => s.IssueAsync(thirdId, thirdStamp, caller))));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => r.State == AssessmentReportLifecycleState.ConcurrencyConflict));
        var second = await LoadRevisionAsync(fixture, pair.ReplacementId);
        var third = await LoadRevisionAsync(fixture, thirdId);
        var predecessor = await LoadRevisionAsync(fixture, pair.PredecessorId);
        Assert.Single(new[] { second, third }, r => r.Status == AssessmentReportRevisionStatus.Issued);
        var winner = second.Status == AssessmentReportRevisionStatus.Issued ? second : third;
        var loser = winner.Id == second.Id ? third : second;
        Assert.Equal(AssessmentReportRevisionStatus.Approved, loser.Status);
        Assert.Null(loser.IssuedUtc);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, predecessor.Status);
        Assert.Equal(winner.Id, predecessor.SupersededByRevisionId);
        Assert.Equal(1, await IssuedCountAsync(fixture, chain.ReportId));
        Assert.Single((await HistoryAsync(fixture, pair.PredecessorId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Superseded));
        Assert.Single((await HistoryAsync(fixture, winner.Id)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
        Assert.Empty((await HistoryAsync(fixture, loser.Id)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Predecessor_Modified_After_It_Is_Read_Returns_ConcurrencyConflict_And_Persists_Nothing()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var pair = await CreateIssuedPairAsync(fixture, chain, caller, T2);
        var interloperStamp = Guid.NewGuid();

        var result = await RunHookedAsync(fixture, new ManualTimeProvider(T3),
            async () =>
            {
                // Runs after the service has read the predecessor and before its first save.
                await using var other = fixture.CreateDbContext();
                var changed = await other.Set<AssessmentReportRevision>().Where(r => r.Id == pair.PredecessorId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConcurrencyStamp, interloperStamp));
                Assert.Equal(1, changed);
            },
            s => s.IssueAsync(pair.ReplacementId, pair.ReplacementStamp, caller));

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        var predecessor = await LoadRevisionAsync(fixture, pair.PredecessorId);
        var replacement = await LoadRevisionAsync(fixture, pair.ReplacementId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, predecessor.Status);
        Assert.Null(predecessor.SupersededByRevisionId);
        Assert.Equal(interloperStamp, predecessor.ConcurrencyStamp);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, replacement.Status);
        Assert.Equal(pair.ReplacementStamp, replacement.ConcurrencyStamp);
        Assert.Empty((await HistoryAsync(fixture, pair.PredecessorId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Superseded));
        Assert.Empty((await HistoryAsync(fixture, pair.ReplacementId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Replacement_History_Insert_Failure_Rolls_Back_Both_Revision_Updates()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var pair = await CreateIssuedPairAsync(fixture, chain, caller, T2);
        var predecessorBefore = await LoadRevisionAsync(fixture, pair.PredecessorId);

        // Conflicting Approved -> Issued event for the replacement makes the service's history insert violate
        // UX_AssessmentReportTransitionHistory_Revision_NewStatus after both revision updates have been saved.
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportTransitionHistory""
(""Id"", ""AssessmentReportId"", ""AssessmentReportRevisionId"", ""EventType"", ""PreviousStatus"", ""NewStatus"", ""ActorUserId"", ""OccurredUtc"", ""Reason"", ""ReplacedRevisionId"", ""ReplacementRevisionId"", ""CorrelationId"")
VALUES ({Guid.NewGuid()}, {chain.ReportId}, {pair.ReplacementId}, {(short)2}, {(short)2}, {(short)3}, {"seed-actor"}, {DateTime.UtcNow}, {(string?)null}, {(Guid?)null}, {(Guid?)null}, {"seed-correlation"})");
        }

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            RunAsync(fixture, new ManualTimeProvider(T3), s => s.IssueAsync(pair.ReplacementId, pair.ReplacementStamp, caller)));

        var predecessor = await LoadRevisionAsync(fixture, pair.PredecessorId);
        var replacement = await LoadRevisionAsync(fixture, pair.ReplacementId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, predecessor.Status);
        Assert.Null(predecessor.SupersededByRevisionId);
        Assert.Equal(predecessorBefore.ConcurrencyStamp, predecessor.ConcurrencyStamp);
        Assert.Equal(predecessorBefore.UpdatedUtc, predecessor.UpdatedUtc);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, replacement.Status);
        Assert.Null(replacement.IssuedUtc);
        Assert.Equal(pair.ReplacementStamp, replacement.ConcurrencyStamp);
        Assert.Equal(1, await IssuedCountAsync(fixture, chain.ReportId));
        Assert.Empty((await HistoryAsync(fixture, pair.PredecessorId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Superseded));
        Assert.Equal("seed-actor", Assert.Single((await HistoryAsync(fixture, pair.ReplacementId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued)).ActorUserId);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Deferred_Trigger_Failure_At_Commit_Rolls_Back_Everything()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        // The service's clock guard passes (T3 >= T2 when it reads the predecessor). After that read, the predecessor's
        // IssuedUtc is moved later without touching its concurrency stamp, so the replacement ends up issued before the
        // predecessor. Only the deferred trigger can catch this, and it must do so at commit.
        var pair = await CreateIssuedPairAsync(fixture, chain, caller, T2);
        var predecessorBefore = await LoadRevisionAsync(fixture, pair.PredecessorId);
        var laterIssuedUtc = T3.UtcDateTime.AddHours(1);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            RunHookedAsync(fixture, new ManualTimeProvider(T3),
                async () =>
                {
                    await using var other = fixture.CreateDbContext();
                    var changed = await other.Set<AssessmentReportRevision>().Where(r => r.Id == pair.PredecessorId)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.IssuedUtc, laterIssuedUtc));
                    Assert.Equal(1, changed);
                },
                s => s.IssueAsync(pair.ReplacementId, pair.ReplacementStamp, caller)));

        var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
        Assert.NotNull(pg);
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, pg!.SqlState);
        Assert.Contains("was issued before superseded revision", pg.MessageText);

        var predecessor = await LoadRevisionAsync(fixture, pair.PredecessorId);
        var replacement = await LoadRevisionAsync(fixture, pair.ReplacementId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, predecessor.Status);
        Assert.Null(predecessor.SupersededByRevisionId);
        Assert.Equal(predecessorBefore.ConcurrencyStamp, predecessor.ConcurrencyStamp);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, replacement.Status);
        Assert.Null(replacement.IssuedUtc);
        Assert.Equal(pair.ReplacementStamp, replacement.ConcurrencyStamp);
        Assert.Equal(1, await IssuedCountAsync(fixture, chain.ReportId));
        Assert.Empty((await HistoryAsync(fixture, pair.PredecessorId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Superseded));
        Assert.Empty((await HistoryAsync(fixture, pair.ReplacementId)).Where(h => h.NewStatus == AssessmentReportRevisionStatus.Issued));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Superseded_Predecessor_And_Lower_Numbered_Approved_Revision_Cannot_Be_Used_For_Replacement()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var pair = await CreateIssuedPairAsync(fixture, chain, caller, T2);
        Assert.True((await RunAsync(fixture, new ManualTimeProvider(T3), s => s.IssueAsync(pair.ReplacementId, pair.ReplacementStamp, caller))).Succeeded);
        var superseded = await LoadRevisionAsync(fixture, pair.PredecessorId);

        var result = await RunAsync(fixture, new ManualTimeProvider(T3), s => s.IssueAsync(pair.PredecessorId, superseded.ConcurrencyStamp, caller));

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, (await LoadRevisionAsync(fixture, pair.PredecessorId)).Status);
        Assert.Equal(1, await IssuedCountAsync(fixture, chain.ReportId));
    }

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

        var gate = new Rendezvous(2);
        var results = await Task.WhenAll(
            Task.Run(() => RunHookedAsync(fixture, time, gate.ArriveAsync, s => s.IssueAsync(chain.RevisionId, firstStamp, caller))),
            Task.Run(() => RunHookedAsync(fixture, time, gate.ArriveAsync, s => s.IssueAsync(secondId, secondStamp, caller))));

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
    public async Task Issuing_Over_An_Issued_Revision_Replaces_It_And_Unique_Index_Still_Rejects_Direct_Update()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var time = new ManualTimeProvider(T1);
        var secondId = await CloneDraftRevisionAsync(fixture, new AssessmentReportRevisionSource(chain.RevisionId), 2);
        var thirdId = await CloneDraftRevisionAsync(fixture, new AssessmentReportRevisionSource(chain.RevisionId), 3);
        var firstStamp = await ApproveAsync(fixture, chain.RevisionId, caller, time);
        var secondStamp = await ApproveAsync(fixture, secondId, caller, time);
        await ApproveAsync(fixture, thirdId, caller, time);
        Assert.True((await RunAsync(fixture, time, s => s.IssueAsync(chain.RevisionId, firstStamp, caller))).Succeeded);

        var replaced = await RunAsync(fixture, new ManualTimeProvider(T2), s => s.IssueAsync(secondId, secondStamp, caller));

        Assert.True(replaced.Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, (await LoadRevisionAsync(fixture, chain.RevisionId)).Status);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await LoadRevisionAsync(fixture, secondId)).Status);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await LoadRevisionAsync(fixture, thirdId)).Status);

        await using var db = fixture.CreateDbContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 3, ""IssuedUtc"" = {DateTime.UtcNow}, ""IssuedByUserId"" = {"direct"} WHERE ""Id"" = {thirdId}"));
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