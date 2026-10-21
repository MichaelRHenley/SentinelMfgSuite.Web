using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Database-level enforcement of correction lineage (BasedOnRevisionId), CorrectionReason and the one-active-revision
/// index. Rows are seeded with raw SQL (no correction service exists). The "source has a lower revision number" rule is
/// intentionally not a database constraint; it is validated by the future creation service.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
public sealed class AssessmentReportCorrectionLineagePostgresIntegrationTests
{
    private const short Draft = 0, InReview = 1, Approved = 2, Issued = 3, Superseded = 4, Rejected = 5;
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string RestrictViolation = "23001";

    private static async Task AssertViolationAsync(Func<Task> action, string sqlState)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(action);
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.NotNull(pg);
        Assert.Equal(sqlState, pg!.SqlState);
    }

    private static async Task<int> ExecuteAsync(PostgresIntegrationTestFixture fixture, FormattableString sql)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    private static async Task<Guid> InsertAsync(
        PostgresIntegrationTestFixture fixture,
        Guid reportId,
        int number,
        short status,
        Guid? basedOn = null,
        string? reason = null,
        Guid? id = null,
        Guid? supersededBy = null)
    {
        var revisionId = id ?? Guid.NewGuid();
        var now = DateTime.UtcNow;
        DateTime? submitted = status is InReview or Approved or Issued or Superseded or Rejected ? now : null;
        DateTime? approved = status is Approved or Issued or Superseded ? now : null;
        DateTime? issued = status is Issued or Superseded ? now : null;
        var submittedBy = submitted is null ? null : "submitter";
        var approvedBy = approved is null ? null : "approver";
        var issuedBy = issued is null ? null : "issuer";
        await ExecuteAsync(fixture, $@"INSERT INTO ""AssessmentReportRevisions""
(""Id"", ""AssessmentReportId"", ""RevisionNumber"", ""Status"", ""ExecutiveSummary"", ""ScopeAndMethodology"", ""AssumptionsAndLimitations"", ""NextSteps"",
 ""CreatedUtc"", ""UpdatedUtc"", ""CreatedByUserId"", ""UpdatedByUserId"", ""SubmittedForReviewUtc"", ""SubmittedForReviewByUserId"",
 ""ApprovedUtc"", ""ApprovedByUserId"", ""IssuedUtc"", ""IssuedByUserId"", ""SupersededByRevisionId"", ""BasedOnRevisionId"", ""CorrectionReason"", ""ConcurrencyStamp"")
VALUES ({revisionId}, {reportId}, {number}, {status}, '', '', '', '', {now}, {now}, 'lineage-test', 'lineage-test', {submitted}, {submittedBy},
 {approved}, {approvedBy}, {issued}, {issuedBy}, {supersededBy}, {basedOn}, {reason}, {Guid.NewGuid()})");
        return revisionId;
    }

    /// <summary>Moves the service-created Draft revision 1 to Issued so the report has a legitimate Issued source.</summary>
    private static async Task IssueInitialAsync(PostgresIntegrationTestFixture fixture, Guid revisionId)
    {
        var now = DateTime.UtcNow;
        await ExecuteAsync(fixture, $@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 3,
 ""SubmittedForReviewUtc"" = {now}, ""SubmittedForReviewByUserId"" = 'submitter', ""ApprovedUtc"" = {now}, ""ApprovedByUserId"" = 'approver',
 ""IssuedUtc"" = {now}, ""IssuedByUserId"" = 'issuer' WHERE ""Id"" = {revisionId}");
    }

    private static async Task<AssessmentReportPostgresIntegrationTests.ReportChain> NewIssuedChainAsync(PostgresIntegrationTestFixture fixture)
    {
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        await IssueInitialAsync(fixture, chain.RevisionId);
        return chain;
    }

    private static async Task<PostgresIntegrationTestFixture> NewFixtureAsync()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        return fixture;
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Valid_Correction_Lineage_And_Reason_Are_Accepted_And_Existing_Revisions_Stay_Null()
    {
        var fixture = await NewFixtureAsync();
        var chain = await NewIssuedChainAsync(fixture);
        var correction = await InsertAsync(fixture, chain.ReportId, 2, Draft, basedOn: chain.RevisionId, reason: new string('x', 2000));
        var noReason = await InsertAsync(fixture, chain.ReportId, 3, Rejected, basedOn: chain.RevisionId);

        await using var db = fixture.CreateDbContext();
        var rows = await db.Set<SentinelMfgSuite.Web.Models.Assessments.AssessmentReportRevision>().AsNoTracking()
            .Where(r => r.AssessmentReportId == chain.ReportId).ToListAsync();
        Assert.Null(rows.Single(r => r.Id == chain.RevisionId).BasedOnRevisionId);
        Assert.Null(rows.Single(r => r.Id == chain.RevisionId).CorrectionReason);
        Assert.Equal(chain.RevisionId, rows.Single(r => r.Id == correction).BasedOnRevisionId);
        Assert.Equal(2000, rows.Single(r => r.Id == correction).CorrectionReason!.Length);
        Assert.Null(rows.Single(r => r.Id == noReason).CorrectionReason);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Cross_Report_Lineage_Is_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var chain = await NewIssuedChainAsync(fixture);
        var other = await NewIssuedChainAsync(fixture);
        await AssertViolationAsync(
            () => InsertAsync(fixture, chain.ReportId, 2, Draft, basedOn: other.RevisionId), ForeignKeyViolation);
        await AssertViolationAsync(
            () => InsertAsync(fixture, chain.ReportId, 2, Draft, basedOn: Guid.NewGuid()), ForeignKeyViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Self_Reference_Is_Rejected_On_Insert_And_Update()
    {
        var fixture = await NewFixtureAsync();
        var chain = await NewIssuedChainAsync(fixture);
        var id = Guid.NewGuid();
        await AssertViolationAsync(
            () => InsertAsync(fixture, chain.ReportId, 2, Draft, basedOn: id, id: id), CheckViolation);
        await AssertViolationAsync(
            () => ExecuteAsync(fixture, $@"UPDATE ""AssessmentReportRevisions"" SET ""BasedOnRevisionId"" = ""Id"" WHERE ""Id"" = {chain.RevisionId}"),
            CheckViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Invalid_Correction_Reason_Is_Rejected()
    {
        var fixture = await NewFixtureAsync();
        var chain = await NewIssuedChainAsync(fixture);

        // Initial revision (no lineage) must not carry a reason.
        await AssertViolationAsync(
            () => ExecuteAsync(fixture, $@"UPDATE ""AssessmentReportRevisions"" SET ""CorrectionReason"" = 'reason' WHERE ""Id"" = {chain.RevisionId}"),
            CheckViolation);
        await AssertViolationAsync(
            () => InsertAsync(fixture, chain.ReportId, 2, Draft, reason: "reason without source"), CheckViolation);
        // Blank / whitespace reason.
        await AssertViolationAsync(
            () => InsertAsync(fixture, chain.ReportId, 2, Draft, basedOn: chain.RevisionId, reason: "   "), CheckViolation);
        // Over 2,000 characters (column length or check; either way rejected).
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => InsertAsync(fixture, chain.ReportId, 2, Draft, basedOn: chain.RevisionId, reason: new string('x', 2001)));
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.NotNull(pg);
        Assert.Contains(pg!.SqlState, new[] { CheckViolation, "22001" });
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Duplicate_Active_Revision_Is_Rejected_For_Each_Active_Status()
    {
        var fixture = await NewFixtureAsync();
        var chain = await NewIssuedChainAsync(fixture);
        await InsertAsync(fixture, chain.ReportId, 2, Draft, basedOn: chain.RevisionId);
        await AssertViolationAsync(() => InsertAsync(fixture, chain.ReportId, 3, Draft, basedOn: chain.RevisionId), UniqueViolation);
        await AssertViolationAsync(() => InsertAsync(fixture, chain.ReportId, 3, InReview, basedOn: chain.RevisionId), UniqueViolation);
        await AssertViolationAsync(() => InsertAsync(fixture, chain.ReportId, 3, Approved, basedOn: chain.RevisionId), UniqueViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Active_Slot_Is_Per_Report()
    {
        var fixture = await NewFixtureAsync();
        var a = await NewIssuedChainAsync(fixture);
        var b = await NewIssuedChainAsync(fixture);
        await InsertAsync(fixture, a.ReportId, 2, Draft, basedOn: a.RevisionId);
        await InsertAsync(fixture, b.ReportId, 2, Draft, basedOn: b.RevisionId);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Rejected_Correction_Frees_The_Active_Slot()
    {
        var fixture = await NewFixtureAsync();
        var chain = await NewIssuedChainAsync(fixture);
        var first = await InsertAsync(fixture, chain.ReportId, 2, Draft, basedOn: chain.RevisionId);
        var now = DateTime.UtcNow;
        await ExecuteAsync(fixture, $@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 5, ""SubmittedForReviewUtc"" = {now}, ""SubmittedForReviewByUserId"" = 'submitter' WHERE ""Id"" = {first}");
        var second = await InsertAsync(fixture, chain.ReportId, 3, Draft, basedOn: chain.RevisionId, reason: "retry");

        await using var db = fixture.CreateDbContext();
        Assert.Equal(2, await db.Set<SentinelMfgSuite.Web.Models.Assessments.AssessmentReportRevision>()
            .CountAsync(r => r.AssessmentReportId == chain.ReportId && r.BasedOnRevisionId == chain.RevisionId));
        Assert.NotEqual(first, second);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Issued_And_Superseded_Coexist_With_An_Active_Correction()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        // Chain revision 1 (Draft) becomes Superseded by an Issued revision 2; revision 3 is the active correction.
        var issuedId = Guid.NewGuid();
        await ExecuteAsync(fixture, $@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 5, ""SubmittedForReviewUtc"" = {DateTime.UtcNow}, ""SubmittedForReviewByUserId"" = 'submitter' WHERE ""Id"" = {chain.RevisionId}");
        await InsertAsync(fixture, chain.ReportId, 2, Issued, id: issuedId);
        var correction = await InsertAsync(fixture, chain.ReportId, 3, Draft, basedOn: issuedId);

        await using var db = fixture.CreateDbContext();
        var statuses = await db.Set<SentinelMfgSuite.Web.Models.Assessments.AssessmentReportRevision>().AsNoTracking()
            .Where(r => r.AssessmentReportId == chain.ReportId).Select(r => (int)r.Status).ToListAsync();
        Assert.Contains((int)Issued, statuses);
        Assert.Contains((int)Draft, statuses);
        Assert.NotEqual(Guid.Empty, correction);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Source_Revision_Cannot_Be_Deleted_While_Referenced()
    {
        var fixture = await NewFixtureAsync();
        var chain = await NewIssuedChainAsync(fixture);
        await InsertAsync(fixture, chain.ReportId, 2, Draft, basedOn: chain.RevisionId);
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => ExecuteAsync(fixture, $@"DELETE FROM ""AssessmentReportRevisions"" WHERE ""Id"" = {chain.RevisionId}"));
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.NotNull(pg);
        Assert.Contains(pg!.SqlState, new[] { ForeignKeyViolation, RestrictViolation, "P0001" });
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Existing_Issuance_Constraints_Are_Preserved()
    {
        var fixture = await NewFixtureAsync();
        var chain = await NewIssuedChainAsync(fixture);
        // Second Issued revision for the same report is still rejected by the current-Issued index.
        await AssertViolationAsync(() => InsertAsync(fixture, chain.ReportId, 2, Issued), UniqueViolation);
        // Status/stamp and status-value checks still apply.
        await AssertViolationAsync(
            () => ExecuteAsync(fixture, $@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 9 WHERE ""Id"" = {chain.RevisionId}"),
            CheckViolation);
        // Superseded requires a replacement link.
        await AssertViolationAsync(
            () => ExecuteAsync(fixture, $@"UPDATE ""AssessmentReportRevisions"" SET ""Status"" = 4 WHERE ""Id"" = {chain.RevisionId}"),
            CheckViolation);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Migration_Preflight_Detects_Multiple_Active_Revisions_And_Leaves_Data_Unchanged()
    {
        var fixture = await NewFixtureAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var table = "AssessmentReportRevisions_PreflightScratch_" + Guid.NewGuid().ToString("N");

        // Reproduce the migration audit against a scratch table that has no unique index (the legacy shape).
        await using var db = fixture.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync($@"CREATE TEMP TABLE ""{table}"" (""AssessmentReportId"" uuid, ""RevisionNumber"" int, ""Status"" smallint)");
        await db.Database.ExecuteSqlRawAsync($@"INSERT INTO ""{table}"" VALUES ('{chain.ReportId}', 1, 0), ('{chain.ReportId}', 2, 2), ('{chain.ReportId}', 3, 3)");
        var multiple = await db.Database.SqlQueryRaw<long>($@"SELECT count(*) AS ""Value"" FROM (SELECT ""AssessmentReportId"" FROM ""{table}"" WHERE ""Status"" IN (0, 1, 2) GROUP BY ""AssessmentReportId"" HAVING count(*) > 1) d").SingleAsync();
        Assert.Equal(1, multiple);
        var rows = await db.Database.SqlQueryRaw<long>($@"SELECT count(*) AS ""Value"" FROM ""{table}""").SingleAsync();
        Assert.Equal(3, rows);
    }
}
