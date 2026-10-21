using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using Xunit;
using Fixture = SentinelMfgSuite.Web.Tests.AssessmentReportRevisionContentServiceTests.Fixture;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>SQLite tests for the Stage 3c-4 correction-lineage rule enforced by IssueAsync.</summary>
public sealed class AssessmentReportCorrectionIssuanceLineageServiceTests
{
    private static readonly DateTimeOffset T1 = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PassingValidator : IAssessmentReportContentValidator
    {
        public AssessmentReportContentValidationResult Validate(
            SentinelMfgSuite.Web.Models.Assessments.Reports.ConsultantReportContent content,
            AssessmentReportContentValidationContext context)
            => new(Array.Empty<ReportContentValidationIssue>());
    }

    private static AssessmentReportLifecycleService Create(Fixture f)
        => new(f.Db, new PassingValidator(), new ManualTimeProvider(T1));

    private static ClaimsPrincipal Caller(string id = "author-1")
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(ClaimTypes.Role, "AssessmentAdmin")
        }, "test"));

    private static async Task<AssessmentReportRevision> AddAsync(
        Fixture f, int number, AssessmentReportRevisionStatus status, Guid? basedOn)
    {
        var original = f.Revision;
        var now = DateTime.UtcNow;
        var revision = new AssessmentReportRevision
        {
            Id = Guid.NewGuid(),
            AssessmentReportId = original.AssessmentReportId,
            RevisionNumber = number,
            BasedOnRevisionId = basedOn,
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
            CreatedByUserId = "seed",
            UpdatedByUserId = "seed"
        };
        f.Db.Set<AssessmentReportRevision>().Add(revision);
        await f.Db.SaveChangesAsync();
        await RevisionStatusSeed.SetAsync(f.Db, revision, status);
        return revision;
    }

    private static async Task<AssessmentReportRevision> StoredAsync(Fixture f, Guid id)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options;
        await using var fresh = new ApplicationDbContext(options);
        return await fresh.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static async Task<int> HistoryCountAsync(Fixture f)
        => await f.Db.Set<AssessmentReportTransitionHistory>().AsNoTracking()
            .CountAsync(h => h.AssessmentReportId == f.Report.Id);

    [Fact]
    public async Task Correction_Based_On_Current_Issued_Replaces_It()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Issued);
        var correction = await AddAsync(f, 2, AssessmentReportRevisionStatus.Approved, f.Revision.Id);

        var result = await Create(f).IssueAsync(correction.Id, correction.ConcurrencyStamp, Caller());

        Assert.True(result.Succeeded);
        Assert.Equal(f.Revision.Id, result.SupersededRevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, (await StoredAsync(f, f.Revision.Id)).Status);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, correction.Id)).Status);
    }

    [Fact]
    public async Task Correction_Based_On_A_Different_Revision_Is_Rejected_Without_Changes()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Issued);
        var rejected = await AddAsync(f, 2, AssessmentReportRevisionStatus.Rejected, f.Revision.Id);
        var correction = await AddAsync(f, 3, AssessmentReportRevisionStatus.Approved, rejected.Id);
        var sourceBefore = await StoredAsync(f, f.Revision.Id);
        var historyBefore = await HistoryCountAsync(f);

        var result = await Create(f).IssueAsync(correction.Id, correction.ConcurrencyStamp, Caller());

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        var source = await StoredAsync(f, f.Revision.Id);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, source.Status);
        Assert.Equal(sourceBefore.ConcurrencyStamp, source.ConcurrencyStamp);
        var stored = await StoredAsync(f, correction.Id);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, stored.Status);
        Assert.Equal(correction.ConcurrencyStamp, stored.ConcurrencyStamp);
        Assert.Equal(historyBefore, await HistoryCountAsync(f));
    }

    [Fact]
    public async Task Correction_Based_On_A_Previously_Superseded_Revision_Is_Rejected()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Issued);
        var second = await AddAsync(f, 2, AssessmentReportRevisionStatus.Approved, f.Revision.Id);
        Assert.True((await Create(f).IssueAsync(second.Id, second.ConcurrencyStamp, Caller())).Succeeded);
        var stale = await AddAsync(f, 3, AssessmentReportRevisionStatus.Approved, f.Revision.Id);
        var historyBefore = await HistoryCountAsync(f);

        var result = await Create(f).IssueAsync(stale.Id, stale.ConcurrencyStamp, Caller());

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, second.Id)).Status);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, stale.Id)).Status);
        Assert.Equal(historyBefore, await HistoryCountAsync(f));
    }

    [Fact]
    public async Task Orphaned_Correction_With_No_Current_Issued_Revision_Is_Not_Initial_Issuance()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Rejected);
        var rejected = await AddAsync(f, 2, AssessmentReportRevisionStatus.Rejected, f.Revision.Id);
        var orphan = await AddAsync(f, 3, AssessmentReportRevisionStatus.Approved, rejected.Id);
        var historyBefore = await HistoryCountAsync(f);

        var result = await Create(f).IssueAsync(orphan.Id, orphan.ConcurrencyStamp, Caller());

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, orphan.Id)).Status);
        Assert.Equal(historyBefore, await HistoryCountAsync(f));
    }

    [Fact]
    public async Task Initial_Issuance_With_Null_Lineage_Still_Works()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Approved);

        var result = await Create(f).IssueAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller());

        Assert.True(result.Succeeded);
        Assert.Null(result.SupersededRevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, f.Revision.Id)).Status);
    }

    [Fact]
    public async Task Replacement_Without_Lineage_Is_Rejected_When_An_Issued_Revision_Exists()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Issued);
        var unrelated = await AddAsync(f, 2, AssessmentReportRevisionStatus.Approved, null);

        var result = await Create(f).IssueAsync(unrelated.Id, unrelated.ConcurrencyStamp, Caller());

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, f.Revision.Id)).Status);
    }

    [Fact]
    public async Task Lineage_Check_Does_Not_Bypass_Authorization_Or_Stale_Stamp()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Issued);
        var correction = await AddAsync(f, 2, AssessmentReportRevisionStatus.Approved, f.Revision.Id);

        Assert.Equal(AssessmentReportLifecycleState.NotFound,
            (await Create(f).IssueAsync(correction.Id, correction.ConcurrencyStamp, Caller("someone-else"))).State);
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict,
            (await Create(f).IssueAsync(correction.Id, Guid.NewGuid(), Caller())).State);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, (await StoredAsync(f, f.Revision.Id)).Status);
    }
}
