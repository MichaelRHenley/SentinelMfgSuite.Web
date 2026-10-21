using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Database-level enforcement of revision status, audit stamps and supersession links. Rows are written with
/// raw SQL so the constraints (not the application) are what is under test. Each test uses a fresh report chain;
/// append-only transition history means chains are intentionally left in place, and no triggers are disabled.
/// Fixture categories: (1) "Valid_*"/"Same_Report_Supersession_*" rows are legacy-style fixtures inserted without
/// transition history (valid pre-history revisions; no history is fabricated); (2) the final compatibility test uses a
/// service-created chain with complete lifecycle history; (3) "*_Rejected" tests are intentionally invalid raw-SQL
/// cases that assert the database constraint fires.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
public sealed class AssessmentReportRevisionSchemaPostgresIntegrationTests
{
    private const short Draft = 0, InReview = 1, Approved = 2, Issued = 3, Superseded = 4, Rejected = 5;
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    private static async Task AssertViolationAsync(Func<Task> action, string sqlState)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(action);
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.NotNull(pg);
        Assert.Equal(sqlState, pg!.SqlState);
    }

    private static async Task<Guid> InsertRevisionAsync(
        PostgresIntegrationTestFixture fixture,
        Guid reportId,
        int number,
        short status,
        bool submitted = false,
        bool approved = false,
        bool issued = false,
        Guid? supersededBy = null,
        Guid? id = null,
        string? submittedBy = null,
        DateTime? submittedUtc = null)
    {
        var revisionId = id ?? Guid.NewGuid();
        var now = DateTime.UtcNow;
        var submittedAt = submittedUtc ?? (submitted ? now : (DateTime?)null);
        var submittedActor = submittedBy ?? (submitted ? "submitter" : null);
        var approvedAt = approved ? now : (DateTime?)null;
        var approvedActor = approved ? "approver" : null;
        var issuedAt = issued ? now : (DateTime?)null;
        var issuedActor = issued ? "issuer" : null;
        await using var db = fixture.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportRevisions""
(""Id"", ""AssessmentReportId"", ""RevisionNumber"", ""Status"", ""ExecutiveSummary"", ""ScopeAndMethodology"", ""AssumptionsAndLimitations"", ""NextSteps"",
 ""CreatedUtc"", ""UpdatedUtc"", ""CreatedByUserId"", ""UpdatedByUserId"", ""SubmittedForReviewUtc"", ""SubmittedForReviewByUserId"",
 ""ApprovedUtc"", ""ApprovedByUserId"", ""IssuedUtc"", ""IssuedByUserId"", ""SupersededByRevisionId"", ""ConcurrencyStamp"")
VALUES ({revisionId}, {reportId}, {number}, {status}, '', '', '', '', {now}, {now}, 'schema-test', 'schema-test', {submittedAt}, {submittedActor},
 {approvedAt}, {approvedActor}, {issuedAt}, {issuedActor}, {supersededBy}, {Guid.NewGuid()})");
        return revisionId;
    }

    /// <summary>
    /// Performs a replacement in ONE transaction: supersede the Issued predecessor, issue the Approved replacement and
    /// append the paired history events. Optional arguments deliberately break one element for negative tests.
    /// Failures from the deferred trigger surface from CommitAsync.
    /// </summary>
    private static async Task ReplaceAsync(
        PostgresIntegrationTestFixture fixture,
        Guid reportId,
        Guid oldId,
        Guid newId,
        bool issueReplacement = true,
        bool predecessorHistory = true,
        bool replacementHistory = true,
        string? predecessorCorrelation = "corr-a",
        string? replacementCorrelation = "corr-a",
        Guid? predecessorEventReplacement = null,
        Guid? replacementEventReplaced = null,
        TimeSpan replacementIssuedOffset = default)
    {
        await using var db = fixture.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTime.UtcNow;
        var issuedAt = now + replacementIssuedOffset;
        await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 4, ""SupersededByRevisionId"" = {newId} WHERE ""Id"" = {oldId}");
        if (issueReplacement)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 3, ""IssuedUtc"" = {issuedAt}, ""IssuedByUserId"" = 'issuer' WHERE ""Id"" = {newId}");
        }

        if (predecessorHistory)
        {
            var replacementRef = predecessorEventReplacement ?? newId;
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportTransitionHistory""
(""Id"", ""AssessmentReportId"", ""AssessmentReportRevisionId"", ""EventType"", ""PreviousStatus"", ""NewStatus"", ""ActorUserId"", ""OccurredUtc"", ""Reason"", ""ReplacedRevisionId"", ""ReplacementRevisionId"", ""CorrelationId"")
VALUES ({Guid.NewGuid()}, {reportId}, {oldId}, 2, 3, 4, 'issuer', {now}, NULL, NULL, {replacementRef}, {predecessorCorrelation})");
        }

        if (replacementHistory)
        {
            var replacedRef = replacementEventReplaced ?? oldId;
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportTransitionHistory""
(""Id"", ""AssessmentReportId"", ""AssessmentReportRevisionId"", ""EventType"", ""PreviousStatus"", ""NewStatus"", ""ActorUserId"", ""OccurredUtc"", ""Reason"", ""ReplacedRevisionId"", ""ReplacementRevisionId"", ""CorrelationId"")
VALUES ({Guid.NewGuid()}, {reportId}, {newId}, 2, 2, 3, 'issuer', {now}, NULL, {replacedRef}, NULL, {replacementCorrelation})");
        }

        await tx.CommitAsync();
    }

    private static async Task<(Guid Old, Guid New)> InsertIssuedAndApprovedAsync(PostgresIntegrationTestFixture fixture, Guid reportId, int oldNumber = 2, int newNumber = 3)
    {
        var oldId = await InsertRevisionAsync(fixture, reportId, oldNumber, Issued, true, true, true);
        var newId = await InsertRevisionAsync(fixture, reportId, newNumber, Approved, true, true);
        return (oldId, newId);
    }

    private static async Task<AssessmentReportRevisionStatus> StatusAsync(PostgresIntegrationTestFixture fixture, Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(item => item.Id == id)).Status;
    }

    private static async Task<int> ExecuteAsync(PostgresIntegrationTestFixture fixture, FormattableString sql)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    private static async Task<PostgresIntegrationTestFixture> NewFixtureAsync()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        return fixture;
    }

    [Theory]
    [InlineData(Draft, false, false, false)]
    [InlineData(InReview, true, false, false)]
    [InlineData(Approved, true, true, false)]
    [InlineData(Issued, true, true, true)]
    [InlineData(Rejected, true, false, false)]
    [InlineData(Rejected, true, true, false)]
    [Trait("Category", "PostgresIntegration")]
    public async Task Valid_Status_And_Stamp_Combinations_Are_Accepted(short status, bool submitted, bool approved, bool issued)
    {
        // Superseded rows are covered by the atomic replacement tests (the commit-time trigger requires history).
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        Guid? link = null;

        var id = await InsertRevisionAsync(fixture, chain.ReportId, 2, status, submitted, approved, issued, link);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal((AssessmentReportRevisionStatus)status, stored.Status);
        Assert.Equal(link, stored.SupersededByRevisionId);
    }

    [Theory]
    [InlineData(Draft, true, false, false)]
    [InlineData(Draft, false, true, false)]
    [InlineData(Draft, false, false, true)]
    [InlineData(InReview, false, false, false)]
    [InlineData(InReview, true, true, false)]
    [InlineData(InReview, true, false, true)]
    [InlineData(Approved, true, false, false)]
    [InlineData(Approved, false, true, false)]
    [InlineData(Approved, true, true, true)]
    [InlineData(Issued, true, true, false)]
    [InlineData(Issued, true, false, true)]
    [InlineData(Issued, false, true, true)]
    [InlineData(Superseded, true, true, false)]
    [InlineData(Superseded, true, false, true)]
    [InlineData(Superseded, false, false, false)]
    [InlineData(Rejected, false, false, false)]
    [InlineData(Rejected, true, true, true)]
    [InlineData(Rejected, true, false, true)]
    [InlineData((short)6, false, false, false)]
    [InlineData((short)-1, false, false, false)]
    [Trait("Category", "PostgresIntegration")]
    public async Task Invalid_Status_And_Stamp_Combinations_Are_Rejected(short status, bool submitted, bool approved, bool issued)
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        Guid? link = null;
        if (status == Superseded)
        {
            link = await InsertRevisionAsync(fixture, chain.ReportId, 3, Draft);
        }

        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 2, status, submitted, approved, issued, link),
            CheckViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Timestamp_Without_Actor_Or_Blank_Actor_Is_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);

        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 2, InReview, submittedUtc: DateTime.UtcNow, submittedBy: null),
            CheckViolation);
        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 3, InReview, submitted: true, submittedBy: "   "),
            CheckViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Duplicate_Current_Issued_Revisions_Are_Rejected_But_Other_Reports_Are_Independent()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var otherChain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);

        await InsertRevisionAsync(fixture, chain.ReportId, 2, Issued, true, true, true);
        await InsertRevisionAsync(fixture, otherChain.ReportId, 2, Issued, true, true, true);

        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 3, Issued, true, true, true),
            UniqueViolation);

        var approvedId = await InsertRevisionAsync(fixture, chain.ReportId, 4, Approved, true, true);
        await AssertViolationAsync(
            () => ExecuteAsync(fixture, $@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 3, ""IssuedUtc"" = {DateTime.UtcNow}, ""IssuedByUserId"" = 'issuer' WHERE ""Id"" = {approvedId}"),
            UniqueViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Same_Report_Supersession_Is_Accepted_And_Preserves_Historical_Stamps()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var (oldId, newId) = await InsertIssuedAndApprovedAsync(fixture, chain.ReportId);

        await ReplaceAsync(fixture, chain.ReportId, oldId, newId);

        await using var db = fixture.CreateDbContext();
        var oldRevision = await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(item => item.Id == oldId);
        var newRevision = await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(item => item.Id == newId);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, oldRevision.Status);
        Assert.Equal(newId, oldRevision.SupersededByRevisionId);
        Assert.NotNull(oldRevision.SubmittedForReviewUtc);
        Assert.NotNull(oldRevision.ApprovedUtc);
        Assert.NotNull(oldRevision.IssuedUtc);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, newRevision.Status);
        Assert.Null(newRevision.SupersededByRevisionId);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Cross_Report_Supersession_Reference_Is_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var otherChain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);

        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 2, Superseded, true, true, true, supersededBy: otherChain.RevisionId),
            ForeignKeyViolation);

        var missingId = Guid.NewGuid();
        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 3, Superseded, true, true, true, supersededBy: missingId),
            ForeignKeyViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Self_Supersession_Is_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var id = Guid.NewGuid();

        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 2, Superseded, true, true, true, supersededBy: id, id: id),
            CheckViolation);
    }

    [Theory]
    [InlineData(Draft, false, false, false)]
    [InlineData(InReview, true, false, false)]
    [InlineData(Approved, true, true, false)]
    [InlineData(Issued, true, true, true)]
    [InlineData(Rejected, true, false, false)]
    [Trait("Category", "PostgresIntegration")]
    public async Task Replacement_Link_On_Non_Superseded_Revision_Is_Rejected(short status, bool submitted, bool approved, bool issued)
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);

        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 2, status, submitted, approved, issued, supersededBy: chain.RevisionId),
            CheckViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Superseded_Revision_Without_Replacement_Link_Is_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var issuedId = await InsertRevisionAsync(fixture, chain.ReportId, 2, Issued, true, true, true);

        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 3, Superseded, true, true, true),
            CheckViolation);
        await AssertViolationAsync(
            () => ExecuteAsync(fixture, $@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 4 WHERE ""Id"" = {issuedId}"),
            CheckViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Multiple_Predecessors_Cannot_Name_The_Same_Replacement()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var (oldId, replacementId) = await InsertIssuedAndApprovedAsync(fixture, chain.ReportId, 2, 4);
        await ReplaceAsync(fixture, chain.ReportId, oldId, replacementId);

        // The unique link index rejects a second predecessor immediately, before any commit-time validation.
        await AssertViolationAsync(
            () => InsertRevisionAsync(fixture, chain.ReportId, 3, Superseded, true, true, true, supersededBy: replacementId),
            UniqueViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Superseded_Revision_Cannot_Be_Deleted_Out_From_Under_Its_Link()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var (oldId, replacementId) = await InsertIssuedAndApprovedAsync(fixture, chain.ReportId);
        await ReplaceAsync(fixture, chain.ReportId, oldId, replacementId);

        await AssertViolationAsync(
            () => ExecuteAsync(fixture, $@"DELETE FROM ""AssessmentReportRevisions"" WHERE ""Id"" = {replacementId}"),
            "23503");
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Migration_Applied_Schema_Exists_And_Existing_Service_Created_Chain_Is_Compatible()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var constraints = await db.Database.SqlQuery<string>(
            $@"SELECT conname AS ""Value"" FROM pg_constraint WHERE conrelid = '""AssessmentReportRevisions""'::regclass").ToListAsync();
        foreach (var name in new[]
        {
            "CK_AssessmentReportRevision_StatusValues",
            "CK_AssessmentReportRevision_NoSelfSupersession",
            "CK_AssessmentReportRevision_SupersededLink",
            "CK_AssessmentReportRevision_StampPairs",
            "CK_AssessmentReportRevision_StatusStamps",
            "FK_AssessmentReportRevisions_SupersededBy_SameReport"
        })
        {
            Assert.Contains(name, constraints);
        }

        var indexes = await db.Database.SqlQuery<string>(
            $@"SELECT indexname AS ""Value"" FROM pg_indexes WHERE tablename = 'AssessmentReportRevisions'").ToListAsync();
        Assert.Contains("UX_AssessmentReportRevisions_Report_CurrentIssued", indexes);
        Assert.Contains("UX_AssessmentReportRevisions_SupersededBy", indexes);
        Assert.Contains("IX_AssessmentReportRevisions_Report_SupersededBy", indexes);

        var stored = await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(item => item.Id == chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, stored.Status);
        Assert.Null(stored.SupersededByRevisionId);
    }
}
