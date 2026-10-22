using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Commit-time (deferred constraint trigger) enforcement of atomic supersession. Valid tests build complete
/// lifecycle chains (paired history events in one transaction). Negative tests break exactly one element and expect
/// the failure at COMMIT. Legacy no-history Superseded rows cannot be created once the trigger exists; the boundary
/// test shows unrelated updates on a Superseded revision are not re-validated.
/// No triggers are disabled and no history is modified.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
public sealed class AssessmentReportSupersessionTriggerPostgresIntegrationTests
{
    private const short Draft = 0, Approved = 2, Issued = 3, Superseded = 4, Rejected = 5;
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string RestrictViolation = "23001";

    private static async Task<PostgresIntegrationTestFixture> NewFixtureAsync()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        return fixture;
    }

    private static PostgresException? Pg(Exception ex) => ex as PostgresException ?? ex.InnerException as PostgresException;

    private static async Task AssertCommitViolationAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(action);
        var pg = Pg(ex);
        Assert.NotNull(pg);
        Assert.Equal(CheckViolation, pg!.SqlState);
    }

    private static async Task<(Guid ReportId, Guid Old, Guid New)> NewPairAsync(PostgresIntegrationTestFixture fixture)
    {
        var chain = await AssessmentReportPostgresIntegrationTests.CreateReportWithoutRevisionsAsync(fixture);
        var oldId = await InsertRevisionAsync(fixture, chain.ReportId, 2, Issued);
        var newId = await InsertRevisionAsync(fixture, chain.ReportId, 3, Approved, basedOn: oldId);
        return (chain.ReportId, oldId, newId);
    }

    private static async Task<Guid> InsertRevisionAsync(PostgresIntegrationTestFixture fixture, Guid reportId, int number, short status, bool approvedStamps = false, Guid? basedOn = null, Guid? supersededBy = null)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.AddMinutes(-5);
        var submitted = status != Draft;
        var approved = approvedStamps || status is Approved or Issued or Superseded;
        var issued = status is Issued or Superseded;
        await using var db = fixture.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportRevisions""
(""Id"", ""AssessmentReportId"", ""RevisionNumber"", ""Status"", ""ExecutiveSummary"", ""ScopeAndMethodology"", ""AssumptionsAndLimitations"", ""NextSteps"",
 ""CreatedUtc"", ""UpdatedUtc"", ""CreatedByUserId"", ""UpdatedByUserId"", ""SubmittedForReviewUtc"", ""SubmittedForReviewByUserId"",
 ""ApprovedUtc"", ""ApprovedByUserId"", ""IssuedUtc"", ""IssuedByUserId"", ""SupersededByRevisionId"", ""BasedOnRevisionId"", ""ConcurrencyStamp"")
VALUES ({id}, {reportId}, {number}, {status}, '', '', '', '', {now}, {now}, 'trigger-test', 'trigger-test',
 {(submitted ? now : (DateTime?)null)}, {(submitted ? "submitter" : null)}, {(approved ? now : (DateTime?)null)}, {(approved ? "approver" : null)},
 {(issued ? now : (DateTime?)null)}, {(issued ? "issuer" : null)}, {supersededBy}, {basedOn}, {Guid.NewGuid()})");
        return id;
    }

    private static async Task<AssessmentReportRevisionStatus> StatusAsync(PostgresIntegrationTestFixture fixture, Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(item => item.Id == id)).Status;
    }

    private static async Task InsertHistoryAsync(ApplicationDbContext db, Guid reportId, Guid revisionId, short prev, short next, Guid? replaced, Guid? replacement, string? correlation)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportTransitionHistory""
(""Id"", ""AssessmentReportId"", ""AssessmentReportRevisionId"", ""EventType"", ""PreviousStatus"", ""NewStatus"", ""ActorUserId"", ""OccurredUtc"", ""Reason"", ""ReplacedRevisionId"", ""ReplacementRevisionId"", ""CorrelationId"")
VALUES ({Guid.NewGuid()}, {reportId}, {revisionId}, 2, {prev}, {next}, 'issuer', {DateTime.UtcNow}, NULL, {replaced}, {replacement}, {correlation})");
    }

    private sealed record Options(
        bool Issue = true,
        bool PredecessorHistory = true,
        bool ReplacementHistory = true,
        string? PredecessorCorrelation = "corr-a",
        string? ReplacementCorrelation = "corr-a",
        Guid? PredecessorEventReplacement = null,
        Guid? ReplacementEventReplaced = null,
        TimeSpan IssuedOffset = default,
        Guid? UnrelatedEventRevision = null,
        string? UnrelatedEventCorrelation = null);

    private static async Task ReplaceAsync(PostgresIntegrationTestFixture fixture, Guid reportId, Guid oldId, Guid newId, Options? options = null)
    {
        options ??= new Options();
        await using var db = fixture.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 4, ""SupersededByRevisionId"" = {newId} WHERE ""Id"" = {oldId}");
        if (options.Issue)
        {
            var issuedAt = DateTime.UtcNow + options.IssuedOffset;
            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 3, ""IssuedUtc"" = {issuedAt}, ""IssuedByUserId"" = 'issuer' WHERE ""Id"" = {newId}");
        }

        if (options.PredecessorHistory)
        {
            await InsertHistoryAsync(db, reportId, oldId, 3, 4, null, options.PredecessorEventReplacement ?? newId, options.PredecessorCorrelation);
        }

        if (options.ReplacementHistory)
        {
            await InsertHistoryAsync(db, reportId, newId, 2, 3, options.ReplacementEventReplaced ?? oldId, null, options.ReplacementCorrelation);
        }

        if (options.UnrelatedEventRevision is Guid unrelated)
        {
            await InsertHistoryAsync(db, reportId, unrelated, 0, 1, null, null, options.UnrelatedEventCorrelation);
        }

        await tx.CommitAsync();
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Valid_Atomic_Replacement_With_Paired_History_And_Shared_Correlation_Commits()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);

        await ReplaceAsync(fixture, reportId, oldId, newId);

        Assert.Equal(AssessmentReportRevisionStatus.Superseded, await StatusAsync(fixture, oldId));
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, newId));
        await using var db = fixture.CreateDbContext();
        var events = await db.Set<AssessmentReportTransitionHistory>().AsNoTracking().Where(h => h.AssessmentReportId == reportId && h.EventType == AssessmentReportTransitionEventType.Transition).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Single(events.Select(e => e.CorrelationId).Distinct());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Validation_Is_Deferred_Until_Commit()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);

        await using var db = fixture.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 4, ""SupersededByRevisionId"" = {newId} WHERE ""Id"" = {oldId}");
        // Statement succeeds although the replacement is not yet Issued and no history exists: validation is deferred.
        await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 3, ""IssuedUtc"" = {DateTime.UtcNow}, ""IssuedByUserId"" = 'issuer' WHERE ""Id"" = {newId}");
        await InsertHistoryAsync(db, reportId, oldId, 3, 4, null, newId, "corr-d");
        await InsertHistoryAsync(db, reportId, newId, 2, 3, oldId, null, "corr-d");
        await tx.CommitAsync();

        Assert.Equal(AssessmentReportRevisionStatus.Superseded, await StatusAsync(fixture, oldId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Missing_Predecessor_History_Fails_At_Commit_And_Rolls_Back()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);

        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, reportId, oldId, newId, new Options(PredecessorHistory: false)));

        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, oldId));
        Assert.Equal(AssessmentReportRevisionStatus.Approved, await StatusAsync(fixture, newId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Missing_Replacement_History_Fails_At_Commit()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);

        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, reportId, oldId, newId, new Options(ReplacementHistory: false)));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Incorrect_Predecessor_Event_Replacement_Link_Fails_At_Commit()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        var other = await InsertRevisionAsync(fixture, reportId, 9, Rejected);

        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, reportId, oldId, newId, new Options(PredecessorEventReplacement: other)));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Incorrect_Replacement_Event_Replaced_Link_Fails_At_Commit()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        var other = await InsertRevisionAsync(fixture, reportId, 9, Rejected);

        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, reportId, oldId, newId, new Options(ReplacementEventReplaced: other)));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Different_Correlation_Ids_Fail_At_Commit()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);

        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, reportId, oldId, newId, new Options(ReplacementCorrelation: "corr-b")));
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, oldId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Replacement_Not_Issued_Fails_At_Commit()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);

        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, reportId, oldId, newId, new Options(Issue: false, ReplacementHistory: false)));
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, oldId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Replacement_With_Lower_Revision_Number_Fails_At_Commit()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateReportWithoutRevisionsAsync(fixture);
        var oldId = await InsertRevisionAsync(fixture, chain.ReportId, 5, Issued);
        var newId = await InsertRevisionAsync(fixture, chain.ReportId, 3, Approved, basedOn: oldId);

        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, chain.ReportId, oldId, newId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Replacement_Issued_Before_Predecessor_Fails_At_Commit()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);

        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, reportId, oldId, newId, new Options(IssuedOffset: TimeSpan.FromHours(-1))));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Cross_Report_Replacement_Is_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, _) = await NewPairAsync(fixture);
        var other = await AssessmentReportPostgresIntegrationTests.CreateReportWithoutRevisionsAsync(fixture);
        var otherApproved = await InsertRevisionAsync(fixture, other.ReportId, 5, Approved);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReplaceAsync(fixture, reportId, oldId, otherApproved));
        Assert.Equal(ForeignKeyViolation, Pg(ex)!.SqlState);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, oldId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Supersession_Without_Replacement_Is_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var (_, oldId, _) = await NewPairAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 4 WHERE ""Id"" = {oldId}"));
        Assert.Equal(CheckViolation, Pg(ex)!.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Two_Predecessors_Sharing_One_Replacement_Are_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        await ReplaceAsync(fixture, reportId, oldId, newId);
        var secondOld = await InsertRevisionAsync(fixture, reportId, 4, Rejected, approvedStamps: true);

        // Status, stamps and link satisfy every check constraint, so only the unique replacement-link index can fire.
        await using var db = fixture.CreateDbContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 4, ""IssuedUtc"" = {DateTime.UtcNow}, ""IssuedByUserId"" = 'issuer', ""SupersededByRevisionId"" = {newId} WHERE ""Id"" = {secondOld}"));
        var pg = Pg(ex)!;
        Assert.Equal(UniqueViolation, pg.SqlState);
        Assert.Equal("UX_AssessmentReportRevisions_SupersededBy", pg.ConstraintName);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Failed_Replacement_Rolls_Back_Both_Halves_And_History()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);

        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, reportId, oldId, newId, new Options(ReplacementCorrelation: "other")));

        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, oldId));
        Assert.Equal(AssessmentReportRevisionStatus.Approved, await StatusAsync(fixture, newId));
        await using var db = fixture.CreateDbContext();
        Assert.Empty(await db.Set<AssessmentReportTransitionHistory>().AsNoTracking().Where(h => h.AssessmentReportId == reportId && h.EventType == AssessmentReportTransitionEventType.Transition).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Replacement_Cannot_Leave_Issued_In_A_Later_Transaction()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        await ReplaceAsync(fixture, reportId, oldId, newId);

        await using var db = fixture.CreateDbContext();
        await AssertCommitViolationAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 5, ""IssuedUtc"" = NULL, ""IssuedByUserId"" = NULL WHERE ""Id"" = {newId}");
            await tx.CommitAsync();
        });
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, newId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Existing_Issued_Revision_Frozen_Content_Update_Is_Rejected_By_The_Immutability_Guard()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var issued = await InsertRevisionAsync(fixture, chain.ReportId, 2, Issued);

        await using var db = fixture.CreateDbContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""ExecutiveSummary"" = 'edited' WHERE ""Id"" = {issued}"));
        Assert.Equal("23514", (ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException)?.SqlState);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, issued));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Frozen_Content_Update_On_Superseded_Revision_Is_Rejected_By_The_Immutability_Guard()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        await ReplaceAsync(fixture, reportId, oldId, newId);

        await using var db = fixture.CreateDbContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""ExecutiveSummary"" = 'edited' WHERE ""Id"" = {oldId}"));
        Assert.Equal("23514", (ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException)?.SqlState);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, await StatusAsync(fixture, oldId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Unrelated_Events_Sharing_A_Correlation_Id_Do_Not_Satisfy_Pairing()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        var other = await InsertRevisionAsync(fixture, reportId, 9, Rejected);

        // The replacement event carries a different correlation id; an unrelated Draft->InReview event carries corr-a.
        await AssertCommitViolationAsync(() => ReplaceAsync(fixture, reportId, oldId, newId,
            new Options(ReplacementCorrelation: "corr-b", UnrelatedEventRevision: other, UnrelatedEventCorrelation: "corr-a")));
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, oldId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Repeated_Supersession_Of_The_Same_Predecessor_Is_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        await ReplaceAsync(fixture, reportId, oldId, newId);
        var third = await InsertRevisionAsync(fixture, reportId, 4, Rejected, approvedStamps: true);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReplaceAsync(fixture, reportId, oldId, third, new Options(PredecessorCorrelation: "corr-r", ReplacementCorrelation: "corr-r")));
        Assert.Equal(CheckViolation, Pg(ex)!.SqlState);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, await StatusAsync(fixture, oldId));
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, newId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Superseded_Revision_Is_Terminal_Even_Within_One_Transaction()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        await ReplaceAsync(fixture, reportId, oldId, newId);

        await using var db = fixture.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 3, ""SupersededByRevisionId"" = NULL WHERE ""Id"" = {oldId}"));
        Assert.Equal(CheckViolation, Pg(ex)!.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Replacement_Revision_Cannot_Be_Deleted_After_Issuance()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        await ReplaceAsync(fixture, reportId, oldId, newId);

        await using var db = fixture.CreateDbContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""AssessmentReportRevisions"" WHERE ""Id"" = {newId}"));
        Assert.Equal(ForeignKeyViolation, Pg(ex)!.SqlState);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, newId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Concurrent_Replacements_Of_One_Predecessor_Allow_Exactly_One()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        var rival = await InsertRevisionAsync(fixture, reportId, 4, Rejected, approvedStamps: true);

        await using var dbA = fixture.CreateDbContext();
        await using var txA = await dbA.Database.BeginTransactionAsync();
        await dbA.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 4, ""SupersededByRevisionId"" = {newId} WHERE ""Id"" = {oldId}");

        // B blocks on A's row lock; once A commits, B's update sees a Superseded row and the terminal trigger rejects it.
        var rivalTask = ReplaceAsync(fixture, reportId, oldId, rival, new Options(PredecessorCorrelation: "corr-b", ReplacementCorrelation: "corr-b"));
        await WaitForBlockedSessionAsync(fixture, rivalTask);
        await dbA.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 3, ""IssuedUtc"" = {DateTime.UtcNow}, ""IssuedByUserId"" = 'issuer' WHERE ""Id"" = {newId}");
        await InsertHistoryAsync(dbA, reportId, oldId, 3, 4, null, newId, "corr-a");
        await InsertHistoryAsync(dbA, reportId, newId, 2, 3, oldId, null, "corr-a");
        await txA.CommitAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => rivalTask);
        Assert.Equal(CheckViolation, Pg(ex)!.SqlState);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, newId));
        Assert.Equal(AssessmentReportRevisionStatus.Rejected, await StatusAsync(fixture, rival));
    }

    private static async Task WaitForBlockedSessionAsync(PostgresIntegrationTestFixture fixture, Task rivalTask)
    {
        await using var monitor = fixture.CreateDbContext();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(rivalTask.IsCompleted, "Rival transaction finished before being blocked by the first transaction.");
            var blocked = await monitor.Database.SqlQuery<int>(
                $@"SELECT COUNT(*)::int AS ""Value"" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> pg_backend_pid()").SingleAsync();
            if (blocked > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Rival transaction never blocked on the first transaction's row lock.");
    }

    private static async Task AssertLineageViolationAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(action);
        var pg = Pg(ex);
        Assert.NotNull(pg);
        Assert.Equal(CheckViolation, pg!.SqlState);
        Assert.Contains("must be based on the revision it supersedes", pg.MessageText);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Correct_Lineage_Replacement_Commits_And_Is_Persisted()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);

        await ReplaceAsync(fixture, reportId, oldId, newId);

        await using var db = fixture.CreateDbContext();
        var replacement = await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == newId);
        Assert.Equal(oldId, replacement.BasedOnRevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, await StatusAsync(fixture, oldId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Null_Lineage_Replacement_Fails_At_Commit_And_Rolls_Back_Everything()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateReportWithoutRevisionsAsync(fixture);
        var oldId = await InsertRevisionAsync(fixture, chain.ReportId, 2, Issued);
        var newId = await InsertRevisionAsync(fixture, chain.ReportId, 3, Approved);

        await AssertLineageViolationAsync(() => ReplaceAsync(fixture, chain.ReportId, oldId, newId));

        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, oldId));
        Assert.Equal(AssessmentReportRevisionStatus.Approved, await StatusAsync(fixture, newId));
        await using var db = fixture.CreateDbContext();
        Assert.Null((await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == oldId)).SupersededByRevisionId);
        Assert.Empty(await db.Set<AssessmentReportTransitionHistory>().AsNoTracking().Where(h => h.AssessmentReportId == chain.ReportId && h.EventType == AssessmentReportTransitionEventType.Transition).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Wrong_Non_Null_Lineage_Replacement_Fails_At_Commit_And_Rolls_Back_Everything()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateReportWithoutRevisionsAsync(fixture);
        var earlier = await InsertRevisionAsync(fixture, chain.ReportId, 1, Rejected, approvedStamps: true);
        var oldId = await InsertRevisionAsync(fixture, chain.ReportId, 2, Issued);
        var newId = await InsertRevisionAsync(fixture, chain.ReportId, 3, Approved, basedOn: earlier);

        await AssertLineageViolationAsync(() => ReplaceAsync(fixture, chain.ReportId, oldId, newId));

        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, oldId));
        Assert.Equal(AssessmentReportRevisionStatus.Approved, await StatusAsync(fixture, newId));
        await using var db = fixture.CreateDbContext();
        Assert.Empty(await db.Set<AssessmentReportTransitionHistory>().AsNoTracking().Where(h => h.AssessmentReportId == chain.ReportId && h.EventType == AssessmentReportTransitionEventType.Transition).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Cross_Report_Lineage_Cannot_Be_Persisted()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, _) = await NewPairAsync(fixture);
        var other = await NewPairAsync(fixture);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InsertRevisionAsync(fixture, reportId, 7, Rejected, basedOn: other.Old));
        Assert.Equal(ForeignKeyViolation, Pg(ex)!.SqlState);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, await StatusAsync(fixture, oldId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Direct_Insert_As_Superseded_With_Null_Lineage_Replacement_Is_Rejected()
    {
        // Compatibility exploit attempt: a brand-new Superseded row (no migration-time grandfathering) pointing at an
        // existing Issued replacement that has no lineage must be validated strictly.
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateReportWithoutRevisionsAsync(fixture);
        var replacement = await InsertRevisionAsync(fixture, chain.ReportId, 5, Issued);

        await AssertLineageViolationAsync(() => InsertRevisionAsync(fixture, chain.ReportId, 4, Superseded, supersededBy: replacement));

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.Set<AssessmentReportRevision>().AsNoTracking().CountAsync(r => r.AssessmentReportId == chain.ReportId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Validator_Function_Enforces_Lineage()
    {
        var fixture = await NewFixtureAsync();
        await using var db = fixture.CreateDbContext();
        var definition = await db.Database.SqlQuery<string>(
            $@"SELECT pg_get_functiondef('""AssessmentReportRevision_ValidateSupersession""()'::regprocedure) AS ""Value""").SingleAsync();
        Assert.Contains("must be based on the revision it supersedes", definition);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Replacement_Lineage_Cannot_Be_Nulled_Or_Redirected_After_Issuance()
    {
        var fixture = await NewFixtureAsync();
        var (reportId, oldId, newId) = await NewPairAsync(fixture);
        await ReplaceAsync(fixture, reportId, oldId, newId);
        var earlier = await InsertRevisionAsync(fixture, reportId, 1, Rejected, approvedStamps: true);

        await using var db = fixture.CreateDbContext();
        var nulled = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""BasedOnRevisionId"" = NULL WHERE ""Id"" = {newId}"));
        Assert.Equal(CheckViolation, Pg(nulled)!.SqlState);
        var redirected = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""BasedOnRevisionId"" = {earlier} WHERE ""Id"" = {newId}"));
        Assert.Equal(CheckViolation, Pg(redirected)!.SqlState);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(oldId, (await verify.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == newId)).BasedOnRevisionId);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Migration_Applied_Supersession_Triggers_Exist_And_Are_Deferred()
    {
        var fixture = await NewFixtureAsync();
        await using var db = fixture.CreateDbContext();
        var triggers = await db.Database.SqlQuery<string>(
            $@"SELECT tgname AS ""Value"" FROM pg_trigger WHERE tgrelid = '""AssessmentReportRevisions""'::regclass AND tgdeferrable AND tginitdeferred").ToListAsync();
        Assert.Contains("CTR_AssessmentReportRevisions_Supersession_Insert", triggers);
        Assert.Contains("CTR_AssessmentReportRevisions_Supersession_Update", triggers);
        Assert.Contains("CTR_AssessmentReportRevisions_ReplacementStaysIssued", triggers);
    }
}
