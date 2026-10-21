using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using Xunit;
using Fixture = SentinelMfgSuite.Web.Tests.AssessmentReportRevisionContentServiceTests.Fixture;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentReportCorrectionCreationServiceTests
{
    private static readonly DateTimeOffset T1 = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

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

    private static ClaimsPrincipal Caller(string? id = "author-1", string? role = "AssessmentAdmin", bool authenticated = true)
    {
        var claims = new List<Claim>();
        if (id is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(authenticated ? new ClaimsIdentity(claims, "test") : new ClaimsIdentity(claims));
    }

    private static async Task<Fixture> IssuedFixtureAsync()
    {
        var f = await Fixture.CreateAsync("3.0", true);
        f.Revision.ConsultantContentJson = SentinelMfgSuite.Web.Models.Assessments.Reports.ConsultantReportContentSerializer.Serialize(
            SentinelMfgSuite.Web.Models.Assessments.Reports.ConsultantReportContent.CreateEmpty());
        f.Revision.ConsultantContentSchemaVersion = SentinelMfgSuite.Web.Models.Assessments.Reports.ConsultantReportContent.CurrentSchemaVersion;
        f.Revision.ReportTemplateVersion = "t1";
        f.Revision.ExecutiveSummary = "summary";
        f.Revision.ScopeAndMethodology = "scope";
        f.Revision.AssumptionsAndLimitations = "assumptions";
        f.Revision.NextSteps = "next";
        f.Revision.ScoringContractVersion = "c1";
        f.Revision.PdfStorageKey = "pdf/key";
        f.Revision.PdfSha256 = new string('a', 64);
        f.Revision.PdfLengthBytes = 10;
        await f.Db.SaveChangesAsync();
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Issued);
        return f;
    }

    private static async Task<AssessmentReportRevision> LoadAsync(Fixture f, Guid id)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options;
        await using var fresh = new ApplicationDbContext(options);
        return await fresh.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task Creates_Draft_Correction_With_Lineage_Copied_Data_And_Reset_Stamps()
    {
        await using var f = await IssuedFixtureAsync();
        var source = await LoadAsync(f, f.Revision.Id);

        var result = await Create(f).CreateCorrectionAsync(f.Report.Id, source.ConcurrencyStamp, "  fix typo  ", Caller());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RevisionNumber);
        Assert.Equal(source.Id, result.SourceRevisionId);
        var created = await LoadAsync(f, result.RevisionId!.Value);
        Assert.Equal(result.ConcurrencyStamp, created.ConcurrencyStamp);
        Assert.NotEqual(source.ConcurrencyStamp, created.ConcurrencyStamp);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, created.Status);
        Assert.Equal(f.Report.Id, created.AssessmentReportId);
        Assert.Equal(source.Id, created.BasedOnRevisionId);
        Assert.Equal("fix typo", created.CorrectionReason);
        Assert.Equal(source.ScoringSnapshot, created.ScoringSnapshot);
        Assert.Equal(source.ScoringSnapshotSchemaVersion, created.ScoringSnapshotSchemaVersion);
        Assert.Equal(source.ScoringContractVersion, created.ScoringContractVersion);
        Assert.Equal(source.ConsultantContentJson, created.ConsultantContentJson);
        Assert.Equal(source.ConsultantContentSchemaVersion, created.ConsultantContentSchemaVersion);
        Assert.Equal(source.ReportTemplateVersion, created.ReportTemplateVersion);
        Assert.Equal(source.ExecutiveSummary, created.ExecutiveSummary);
        Assert.Equal(source.ScopeAndMethodology, created.ScopeAndMethodology);
        Assert.Equal(source.AssumptionsAndLimitations, created.AssumptionsAndLimitations);
        Assert.Equal(source.NextSteps, created.NextSteps);
        Assert.Null(created.SubmittedForReviewUtc);
        Assert.Null(created.SubmittedForReviewByUserId);
        Assert.Null(created.ApprovedUtc);
        Assert.Null(created.ApprovedByUserId);
        Assert.Null(created.IssuedUtc);
        Assert.Null(created.IssuedByUserId);
        Assert.Null(created.SupersededByRevisionId);
        Assert.Null(created.PdfStorageKey);
        Assert.Null(created.PdfSha256);
        Assert.Null(created.PdfLengthBytes);
        Assert.Equal(T1.UtcDateTime, created.CreatedUtc);
        Assert.Equal(T1.UtcDateTime, created.UpdatedUtc);
        Assert.Equal("author-1", created.CreatedByUserId);
        Assert.Equal("author-1", created.UpdatedByUserId);
    }

    [Fact]
    public async Task Writes_Exactly_One_Created_History_Event_And_Leaves_Source_Unchanged()
    {
        await using var f = await IssuedFixtureAsync();
        var before = await LoadAsync(f, f.Revision.Id);

        var result = await Create(f).CreateCorrectionAsync(f.Report.Id, before.ConcurrencyStamp, null, Caller());

        var after = await LoadAsync(f, f.Revision.Id);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
        Assert.Equal(before.IssuedUtc, after.IssuedUtc);
        Assert.Equal(before.PdfStorageKey, after.PdfStorageKey);
        Assert.Null(after.SupersededByRevisionId);
        var history = await f.Db.Set<AssessmentReportTransitionHistory>().AsNoTracking()
            .Where(h => h.AssessmentReportRevisionId == result.RevisionId).ToListAsync();
        var created = Assert.Single(history);
        Assert.Equal(AssessmentReportTransitionEventType.Created, created.EventType);
        Assert.Null(created.PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, created.NewStatus);
        Assert.Equal(f.Report.Id, created.AssessmentReportId);
        Assert.Equal("author-1", created.ActorUserId);
        Assert.Equal(T1.UtcDateTime, created.OccurredUtc);
        Assert.Null(created.Reason);
        Assert.Null(created.ReplacedRevisionId);
        Assert.Null(created.ReplacementRevisionId);
        Assert.False(string.IsNullOrWhiteSpace(created.CorrelationId));
        Assert.Null((await LoadAsync(f, result.RevisionId!.Value)).CorrectionReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_Reason_Is_Rejected(string reason)
    {
        await using var f = await IssuedFixtureAsync();

        var result = await Create(f).CreateCorrectionAsync(f.Report.Id, f.Revision.ConcurrencyStamp, reason, Caller());

        Assert.Equal(AssessmentReportLifecycleState.InvalidReason, result.State);
        Assert.Equal(1, await f.Db.Set<AssessmentReportRevision>().CountAsync());
    }

    [Fact]
    public async Task Reason_Length_Boundary_Is_Applied_After_Trimming()
    {
        await using var f = await IssuedFixtureAsync();
        var service = Create(f);

        var tooLong = await service.CreateCorrectionAsync(f.Report.Id, f.Revision.ConcurrencyStamp, new string('x', 2001), Caller());
        var ok = await service.CreateCorrectionAsync(f.Report.Id, f.Revision.ConcurrencyStamp, " " + new string('x', 2000) + " ", Caller());

        Assert.Equal(AssessmentReportLifecycleState.InvalidReason, tooLong.State);
        Assert.True(ok.Succeeded);
        Assert.Equal(2000, (await LoadAsync(f, ok.RevisionId!.Value)).CorrectionReason!.Length);
    }

    [Fact]
    public async Task Authorization_Ownership_And_Missing_Report()
    {
        await using var f = await IssuedFixtureAsync();
        var service = Create(f);
        var stamp = f.Revision.ConcurrencyStamp;

        Assert.Equal(AssessmentReportLifecycleState.Forbidden,
            (await service.CreateCorrectionAsync(f.Report.Id, stamp, null, Caller(authenticated: false))).State);
        Assert.Equal(AssessmentReportLifecycleState.Forbidden,
            (await service.CreateCorrectionAsync(f.Report.Id, stamp, null, Caller(role: null))).State);
        Assert.Equal(AssessmentReportLifecycleState.Forbidden,
            (await service.CreateCorrectionAsync(f.Report.Id, stamp, null, Caller(id: null))).State);
        Assert.Equal(AssessmentReportLifecycleState.NotFound,
            (await service.CreateCorrectionAsync(f.Report.Id, stamp, null, Caller("someone-else"))).State);
        Assert.Equal(AssessmentReportLifecycleState.NotFound,
            (await service.CreateCorrectionAsync(Guid.NewGuid(), stamp, null, Caller())).State);
        Assert.Equal(1, await f.Db.Set<AssessmentReportRevision>().CountAsync());
    }

    [Fact]
    public async Task Missing_Issued_Revision_Is_Reported()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);

        var result = await Create(f).CreateCorrectionAsync(f.Report.Id, f.Revision.ConcurrencyStamp, null, Caller());

        Assert.Equal(AssessmentReportLifecycleState.NoIssuedRevision, result.State);
        Assert.Equal(1, await f.Db.Set<AssessmentReportRevision>().CountAsync());
    }

    [Fact]
    public async Task Stale_Source_Stamp_Is_A_Concurrency_Conflict_With_Current_Stamp()
    {
        await using var f = await IssuedFixtureAsync();
        var current = f.Revision.ConcurrencyStamp;

        var result = await Create(f).CreateCorrectionAsync(f.Report.Id, Guid.NewGuid(), null, Caller());

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        Assert.Equal(current, result.SourceConcurrencyStamp);
        Assert.Equal(1, await f.Db.Set<AssessmentReportRevision>().CountAsync());
    }

    [Fact]
    public async Task Existing_Active_Correction_Blocks_Creation()
    {
        await using var f = await IssuedFixtureAsync();
        var service = Create(f);
        var first = await service.CreateCorrectionAsync(f.Report.Id, f.Revision.ConcurrencyStamp, null, Caller());
        Assert.True(first.Succeeded);

        var second = await service.CreateCorrectionAsync(f.Report.Id, f.Revision.ConcurrencyStamp, null, Caller());

        Assert.Equal(AssessmentReportLifecycleState.ActiveCorrectionExists, second.State);
        Assert.Equal(2, await f.Db.Set<AssessmentReportRevision>().CountAsync());
    }

    [Fact]
    public async Task Revision_Number_Is_Never_Reused_After_Rejected_Revisions()
    {
        await using var f = await IssuedFixtureAsync();
        var service = Create(f);
        var first = await service.CreateCorrectionAsync(f.Report.Id, f.Revision.ConcurrencyStamp, null, Caller());
        var draft = await f.Db.Set<AssessmentReportRevision>().SingleAsync(r => r.Id == first.RevisionId);
        var lifecycle = new AssessmentReportLifecycleService(f.Db, new PassingValidator(), new ManualTimeProvider(T1.AddHours(1)));
        var submit = await lifecycle.SubmitForReviewAsync(draft.Id, draft.ConcurrencyStamp, Caller());
        var reject = await lifecycle.RejectAsync(draft.Id, submit.ConcurrencyStamp!.Value, "no", Caller());
        Assert.True(reject.Succeeded);

        var second = await service.CreateCorrectionAsync(f.Report.Id, f.Revision.ConcurrencyStamp, null, Caller());

        Assert.True(second.Succeeded);
        Assert.Equal(3, second.RevisionNumber);
        Assert.Equal(f.Revision.Id, (await LoadAsync(f, second.RevisionId!.Value)).BasedOnRevisionId);
    }

    [Fact]
    public async Task History_Insert_Failure_Rolls_Back_The_Revision()
    {
        await using var f = await IssuedFixtureAsync();
        var table = f.Db.Model.FindEntityType(typeof(AssessmentReportTransitionHistory))!.GetTableName();
#pragma warning disable EF1002
        await f.Db.Database.ExecuteSqlRawAsync(
            $"CREATE TRIGGER fail_history BEFORE INSERT ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'blocked'); END;");
#pragma warning restore EF1002
        var service = Create(f);
        var reportId = f.Report.Id;
        var stamp = f.Revision.ConcurrencyStamp;

        await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateCorrectionAsync(reportId, stamp, null, Caller()));

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options;
        await using var fresh = new ApplicationDbContext(options);
        Assert.Equal(1, await fresh.Set<AssessmentReportRevision>().CountAsync());
        Assert.Empty(f.Db.ChangeTracker.Entries<AssessmentReportRevision>().Where(e => e.State == EntityState.Added));
    }
}
