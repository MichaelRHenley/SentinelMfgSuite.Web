using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Correction-creation tests against the guarded PostgreSQL test database. Transition history is append-only, so
/// chains created here are intentionally left in place. Races are synchronized by holding real row locks and waiting
/// for blocked sessions, never by fixed delays.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
public sealed class AssessmentReportCorrectionCreationPostgresIntegrationTests
{
    private static readonly DateTimeOffset T1 = new(2030, 6, 2, 3, 4, 5, TimeSpan.Zero);

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

    private static async Task<T> RunAsync<T>(PostgresIntegrationTestFixture fixture, DateTimeOffset at, Func<AssessmentReportLifecycleService, Task<T>> action)
    {
        await using var db = fixture.CreateDbContext();
        return await action(new AssessmentReportLifecycleService(db, new PassingValidator(), new ManualTimeProvider(at)));
    }

    private static async Task<AssessmentReportRevision> LoadAsync(PostgresIntegrationTestFixture fixture, Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static async Task<List<AssessmentReportRevision>> RevisionsAsync(PostgresIntegrationTestFixture fixture, Guid reportId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<AssessmentReportRevision>().AsNoTracking()
            .Where(r => r.AssessmentReportId == reportId).OrderBy(r => r.RevisionNumber).ToListAsync();
    }

    private static async Task<int> CreatedEventsAsync(PostgresIntegrationTestFixture fixture, Guid revisionId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<AssessmentReportTransitionHistory>()
            .CountAsync(h => h.AssessmentReportRevisionId == revisionId && h.EventType == AssessmentReportTransitionEventType.Created);
    }

    /// <summary>Report whose revision 1 is Issued.</summary>
    private static async Task<(AssessmentReportPostgresIntegrationTests.ReportChain Chain, System.Security.Claims.ClaimsPrincipal Caller, Guid SourceStamp)> IssuedChainAsync(
        PostgresIntegrationTestFixture fixture)
    {
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var stamp = await ApproveAsync(fixture, chain.RevisionId, caller);
        var issued = await RunAsync(fixture, T1.AddHours(1), s => s.IssueAsync(chain.RevisionId, stamp, caller));
        Assert.True(issued.Succeeded);
        return (chain, caller, issued.ConcurrencyStamp!.Value);
    }

    private static async Task<Guid> ApproveAsync(PostgresIntegrationTestFixture fixture, Guid revisionId, System.Security.Claims.ClaimsPrincipal caller)
    {
        var stamp = (await LoadAsync(fixture, revisionId)).ConcurrencyStamp;
        var submit = await RunAsync(fixture, T1, s => s.SubmitForReviewAsync(revisionId, stamp, caller));
        Assert.True(submit.Succeeded);
        var approve = await RunAsync(fixture, T1, s => s.ApproveAsync(revisionId, submit.ConcurrencyStamp!.Value, caller));
        Assert.True(approve.Succeeded);
        return approve.ConcurrencyStamp!.Value;
    }

    private static async Task WaitForBlockedSessionsAsync(PostgresIntegrationTestFixture fixture, int expected, params Task[] racers)
    {
        await using var monitor = fixture.CreateDbContext();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            Assert.All(racers, t => Assert.False(t.IsCompleted, "A racer finished before being blocked by the lock holder."));
            var blocked = await monitor.Database.SqlQuery<int>(
                $@"SELECT COUNT(*)::int AS ""Value"" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> pg_backend_pid()").SingleAsync();
            if (blocked >= expected)
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail("Racers never blocked on the lock holder.");
    }

    private static string? ConstraintOf(Exception ex)
        => (ex as PostgresException ?? ex.InnerException as PostgresException)?.ConstraintName;

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Creation_Persists_Draft_One_Created_Event_And_Preserves_Issued_Source()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var (chain, caller, sourceStamp) = await IssuedChainAsync(fixture);
        var before = await LoadAsync(fixture, chain.RevisionId);

        var result = await RunAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, "  typo  ", caller));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RevisionNumber);
        var after = await LoadAsync(fixture, chain.RevisionId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
        Assert.Equal(before.IssuedUtc, after.IssuedUtc);
        Assert.Equal(before.IssuedByUserId, after.IssuedByUserId);
        Assert.Equal(before.PdfStorageKey, after.PdfStorageKey);
        Assert.Equal(before.ScoringSnapshot, after.ScoringSnapshot);
        Assert.Equal(before.ConsultantContentJson, after.ConsultantContentJson);
        var created = await LoadAsync(fixture, result.RevisionId!.Value);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, created.Status);
        Assert.Equal(chain.RevisionId, created.BasedOnRevisionId);
        Assert.Equal("typo", created.CorrectionReason);
        Assert.Equal(before.ScoringSnapshot, created.ScoringSnapshot);
        Assert.Equal(before.ConsultantContentJson, created.ConsultantContentJson);
        Assert.Null(created.SubmittedForReviewUtc);
        Assert.Null(created.IssuedUtc);
        Assert.Null(created.PdfStorageKey);
        Assert.Equal(T1.AddHours(2).UtcDateTime, created.CreatedUtc);
        Assert.Equal(1, await CreatedEventsAsync(fixture, created.Id));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Two_Simultaneous_Creations_Allow_Exactly_One()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var (chain, caller, sourceStamp) = await IssuedChainAsync(fixture);

        // Hold the report row lock so both creators are demonstrably in flight before either can proceed.
        await using var holder = fixture.CreateDbContext();
        await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($@"SELECT 1 FROM ""AssessmentReports"" WHERE ""Id"" = {chain.ReportId} FOR UPDATE");
        var a = Task.Run(() => RunAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, "a", caller)));
        var b = Task.Run(() => RunAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, "b", caller)));
        await WaitForBlockedSessionsAsync(fixture, 2, a, b);
        await tx.CommitAsync();

        var results = await Task.WhenAll(a, b);
        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => r.State == AssessmentReportLifecycleState.ActiveCorrectionExists));
        var revisions = await RevisionsAsync(fixture, chain.ReportId);
        Assert.Equal(new[] { 1, 2 }, revisions.Select(r => r.RevisionNumber));
        Assert.Equal(1, await CreatedEventsAsync(fixture, results.Single(r => r.Succeeded).RevisionId!.Value));
        Assert.Equal(AssessmentReportRevisionStatus.Issued, revisions[0].Status);
        Assert.Equal(sourceStamp, revisions[0].ConcurrencyStamp);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Source_Stamp_Update_During_Creation_Is_Detected_After_The_Source_Lock_Is_Released()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var (chain, caller, sourceStamp) = await IssuedChainAsync(fixture);
        var newStamp = Guid.NewGuid();

        await using var holder = fixture.CreateDbContext();
        await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""ConcurrencyStamp"" = {newStamp} WHERE ""Id"" = {chain.RevisionId}");
        var creator = Task.Run(() => RunAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, null, caller)));
        await WaitForBlockedSessionsAsync(fixture, 1, creator);
        await tx.CommitAsync();

        var result = await creator;
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        Assert.Equal(newStamp, result.SourceConcurrencyStamp);
        Assert.Single(await RevisionsAsync(fixture, chain.ReportId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Concurrent_Real_Supersession_During_Creation_Never_Produces_A_Correction()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var (chain, caller, sourceStamp) = await IssuedChainAsync(fixture);
        var first = await RunAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, null, caller));
        var replacementStamp = await ApproveAsync(fixture, first.RevisionId!.Value, caller);

        // Real lifecycle supersession (IssueAsync) races a creator while a third session holds the source row lock.
        await using var holder = fixture.CreateDbContext();
        await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($@"SELECT 1 FROM ""AssessmentReports"" WHERE ""Id"" = {chain.ReportId} FOR UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync($@"SELECT 1 FROM ""AssessmentReportRevisions"" WHERE ""Id"" = {chain.RevisionId} FOR UPDATE");
        var issue = Task.Run(() => RunAsync(fixture, T1.AddHours(3), s => s.IssueAsync(first.RevisionId!.Value, replacementStamp, caller)));
        var creator = Task.Run(() => RunAsync(fixture, T1.AddHours(3), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, null, caller)));
        await WaitForBlockedSessionsAsync(fixture, 2, issue, creator);
        await tx.CommitAsync();

        Assert.True((await issue).Succeeded);
        var result = await creator;
        Assert.False(result.Succeeded);
        Assert.Contains(result.State, new[] { AssessmentReportLifecycleState.ActiveCorrectionExists, AssessmentReportLifecycleState.ConcurrencyConflict });
        Assert.Equal(2, (await RevisionsAsync(fixture, chain.ReportId)).Count);
    }
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Superseded_Source_Is_Not_Used_And_Replacement_Becomes_The_Source()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var (chain, caller, sourceStamp) = await IssuedChainAsync(fixture);
        var first = await RunAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, null, caller));
        var replacementStamp = await ApproveAsync(fixture, first.RevisionId!.Value, caller);
        var issued = await RunAsync(fixture, T1.AddHours(3), s => s.IssueAsync(first.RevisionId!.Value, replacementStamp, caller));
        Assert.True(issued.Succeeded);

        var stale = await RunAsync(fixture, T1.AddHours(4), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, null, caller));
        var next = await RunAsync(fixture, T1.AddHours(4), s => s.CreateCorrectionAsync(chain.ReportId, issued.ConcurrencyStamp!.Value, null, caller));

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, stale.State);
        Assert.True(next.Succeeded);
        Assert.Equal(3, next.RevisionNumber);
        Assert.Equal(first.RevisionId, next.SourceRevisionId);
        Assert.Equal(first.RevisionId, (await LoadAsync(fixture, next.RevisionId!.Value)).BasedOnRevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, (await LoadAsync(fixture, chain.RevisionId)).Status);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Active_Slot_And_Revision_Number_Uniqueness_Are_Enforced_By_Named_Indexes()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var (chain, caller, sourceStamp) = await IssuedChainAsync(fixture);
        var created = await RunAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, null, caller));
        Assert.True(created.Succeeded);

        await using var db = fixture.CreateDbContext();
        var now = DateTime.UtcNow;
        var activeEx = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO ""AssessmentReportRevisions"" (""Id"", ""AssessmentReportId"", ""RevisionNumber"", ""Status"", ""ExecutiveSummary"", ""ScopeAndMethodology"", ""AssumptionsAndLimitations"", ""NextSteps"", ""CreatedUtc"", ""UpdatedUtc"", ""CreatedByUserId"", ""UpdatedByUserId"", ""ConcurrencyStamp"")
               VALUES ({Guid.NewGuid()}, {chain.ReportId}, 9, 0, '', '', '', '', {now}, {now}, 'u', 'u', {Guid.NewGuid()})"));
        Assert.Equal("UX_AssessmentReportRevisions_Report_ActiveRevision", ConstraintOf(activeEx));

        var numberEx = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO ""AssessmentReportRevisions"" (""Id"", ""AssessmentReportId"", ""RevisionNumber"", ""Status"", ""ExecutiveSummary"", ""ScopeAndMethodology"", ""AssumptionsAndLimitations"", ""NextSteps"", ""CreatedUtc"", ""UpdatedUtc"", ""CreatedByUserId"", ""UpdatedByUserId"", ""ConcurrencyStamp"", ""SubmittedForReviewUtc"", ""SubmittedForReviewByUserId"")
               VALUES ({Guid.NewGuid()}, {chain.ReportId}, 2, 5, '', '', '', '', {now}, {now}, 'u', 'u', {Guid.NewGuid()}, {now}, 'u')"));
        Assert.Equal("IX_AssessmentReportRevisions_AssessmentReportId_RevisionNumber", ConstraintOf(numberEx));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task History_Failure_Rolls_Back_Revision_And_Leaves_Source_Untouched()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var (chain, caller, sourceStamp) = await IssuedChainAsync(fixture);
        var before = await LoadAsync(fixture, chain.RevisionId);
        var suffix = chain.ReportId.ToString("N");
        var function = $"fail_history_{suffix}";

        await using (var setup = fixture.CreateDbContext())
        {
#pragma warning disable EF1002
            await setup.Database.ExecuteSqlRawAsync(
                $"CREATE FUNCTION \"{function}\"() RETURNS trigger AS $f$ BEGIN IF NEW.\"AssessmentReportId\" = '{chain.ReportId}'::uuid THEN RAISE EXCEPTION 'blocked history insert'; END IF; RETURN NEW; END $f$ LANGUAGE plpgsql;");
            await setup.Database.ExecuteSqlRawAsync(
                $"CREATE TRIGGER \"{function}\" BEFORE INSERT ON \"AssessmentReportTransitionHistory\" FOR EACH ROW EXECUTE FUNCTION \"{function}\"();");
#pragma warning restore EF1002
        }

        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                RunAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, null, caller)));
        }
        finally
        {
            await using var cleanup = fixture.CreateDbContext();
#pragma warning disable EF1002
            await cleanup.Database.ExecuteSqlRawAsync($"DROP TRIGGER \"{function}\" ON \"AssessmentReportTransitionHistory\";");
            await cleanup.Database.ExecuteSqlRawAsync($"DROP FUNCTION \"{function}\"();");
#pragma warning restore EF1002
        }

        var revisions = await RevisionsAsync(fixture, chain.ReportId);
        Assert.Single(revisions);
        Assert.Equal(before.ConcurrencyStamp, revisions[0].ConcurrencyStamp);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, revisions[0].Status);
        var retry = await RunAsync(fixture, T1.AddHours(3), s => s.CreateCorrectionAsync(chain.ReportId, sourceStamp, null, caller));
        Assert.True(retry.Succeeded);
        Assert.Equal(2, retry.RevisionNumber);
    }
}
