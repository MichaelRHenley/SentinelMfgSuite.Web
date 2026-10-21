using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using Xunit;
using Fixture = SentinelMfgSuite.Web.Tests.AssessmentReportRevisionContentServiceTests.Fixture;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentReportIssuanceServiceTests
{
    private static readonly DateTimeOffset T1 = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubValidator(bool valid) : IAssessmentReportContentValidator
    {
        public List<AssessmentReportContentStage> Stages { get; } = new();
        public AssessmentReportContentValidationResult Validate(
            SentinelMfgSuite.Web.Models.Assessments.Reports.ConsultantReportContent content,
            AssessmentReportContentValidationContext context)
        {
            Stages.Add(context.Stage);
            return new(valid
                ? Array.Empty<ReportContentValidationIssue>()
                : new[] { new ReportContentValidationIssue("x", ReportContentValidationSeverity.Error, "p", "m") });
        }
    }

    private static AssessmentReportLifecycleService Create(Fixture f, IAssessmentReportContentValidator? validator = null)
        => new(f.Db, validator ?? new StubValidator(true), new ManualTimeProvider(T1));

    private static ClaimsPrincipal Caller(string? id, string? role = "AssessmentAdmin", bool authenticated = true)
    {
        var claims = new List<Claim>();
        if (id is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(authenticated ? new ClaimsIdentity(claims, "test") : new ClaimsIdentity(claims));
    }

    private static async Task<Fixture> ApprovedAsync()
    {
        var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, AssessmentReportRevisionStatus.Approved);
        return f;
    }

    private static async Task<List<AssessmentReportTransitionHistory>> HistoryAsync(Fixture f)
        => await f.Db.Set<AssessmentReportTransitionHistory>().AsNoTracking()
            .Where(h => h.AssessmentReportId == f.Report.Id).ToListAsync();

    private static async Task<AssessmentReportRevision> StoredAsync(Fixture f, Guid id)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options;
        await using var fresh = new ApplicationDbContext(options);
        return await fresh.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task Issue_Sets_Status_Stamps_And_Single_History_Event_Without_Touching_Frozen_Data()
    {
        await using var f = await ApprovedAsync();
        var validator = new StubValidator(true);
        var service = Create(f, validator);
        var before = f.Revision;
        var (snapshot, schema, content, contentSchema, template, submittedUtc, submittedBy, approvedUtc, approvedBy) =
            (before.ScoringSnapshot, before.ScoringSnapshotSchemaVersion, before.ConsultantContentJson,
             before.ConsultantContentSchemaVersion, before.ReportTemplateVersion, before.SubmittedForReviewUtc,
             before.SubmittedForReviewByUserId, before.ApprovedUtc, before.ApprovedByUserId);
        var oldStamp = before.ConcurrencyStamp;

        var result = await service.IssueAsync(f.Revision.Id, oldStamp, Caller("author-1"));

        Assert.True(result.Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, result.Status);
        Assert.NotEqual(oldStamp, result.ConcurrencyStamp);
        Assert.Equal(new[] { AssessmentReportContentStage.Issued }, validator.Stages);

        var stored = await StoredAsync(f, f.Revision.Id);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, stored.Status);
        Assert.Equal(T1.UtcDateTime, stored.IssuedUtc);
        Assert.Equal("author-1", stored.IssuedByUserId);
        Assert.Equal(T1.UtcDateTime, stored.UpdatedUtc);
        Assert.Equal("author-1", stored.UpdatedByUserId);
        Assert.Equal(result.ConcurrencyStamp, stored.ConcurrencyStamp);
        Assert.Null(stored.SupersededByRevisionId);
        Assert.Equal(snapshot, stored.ScoringSnapshot);
        Assert.Equal(schema, stored.ScoringSnapshotSchemaVersion);
        Assert.Equal(content, stored.ConsultantContentJson);
        Assert.Equal(contentSchema, stored.ConsultantContentSchemaVersion);
        Assert.Equal(template, stored.ReportTemplateVersion);
        Assert.Equal(submittedUtc, stored.SubmittedForReviewUtc);
        Assert.Equal(submittedBy, stored.SubmittedForReviewByUserId);
        Assert.Equal(approvedUtc, stored.ApprovedUtc);
        Assert.Equal(approvedBy, stored.ApprovedByUserId);
        Assert.Null(stored.PdfStorageKey);
        Assert.Null(stored.PdfSha256);
        Assert.Null(stored.PdfLengthBytes);

        var history = Assert.Single(await HistoryAsync(f));
        Assert.Equal(AssessmentReportTransitionEventType.Transition, history.EventType);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, history.PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, history.NewStatus);
        Assert.Equal(f.Report.Id, history.AssessmentReportId);
        Assert.Equal(f.Revision.Id, history.AssessmentReportRevisionId);
        Assert.Equal("author-1", history.ActorUserId);
        Assert.Equal(T1.UtcDateTime, history.OccurredUtc);
        Assert.Null(history.Reason);
        Assert.Null(history.ReplacedRevisionId);
        Assert.Null(history.ReplacementRevisionId);
        Assert.False(string.IsNullOrWhiteSpace(history.CorrelationId));
    }

    [Fact]
    public async Task Issue_Authorization_And_Ownership()
    {
        await using var f = await ApprovedAsync();
        var service = Create(f);
        var stamp = f.Revision.ConcurrencyStamp;

        Assert.Equal(AssessmentReportLifecycleState.Forbidden, (await service.IssueAsync(f.Revision.Id, stamp, Caller("author-1", authenticated: false))).State);
        Assert.Equal(AssessmentReportLifecycleState.Forbidden, (await service.IssueAsync(f.Revision.Id, stamp, Caller("author-1", role: null))).State);
        Assert.Equal(AssessmentReportLifecycleState.Forbidden, (await service.IssueAsync(f.Revision.Id, stamp, Caller(null))).State);
        Assert.Equal(AssessmentReportLifecycleState.NotFound, (await service.IssueAsync(Guid.NewGuid(), stamp, Caller("author-1"))).State);
        Assert.Equal(AssessmentReportLifecycleState.NotFound, (await service.IssueAsync(f.Revision.Id, stamp, Caller("someone-else"))).State);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, f.Revision.Id)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Issue_With_Missing_Snapshot_Returns_InvalidSourceData()
    {
        await using var f = await ApprovedAsync();
        f.Revision.ScoringSnapshot = null;
        await f.Db.SaveChangesAsync();
        var service = Create(f);

        var result = await service.IssueAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.InvalidSourceData, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, f.Revision.Id)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Issue_With_Failing_Content_Validation_Writes_Nothing()
    {
        await using var f = await ApprovedAsync();
        var service = Create(f, new StubValidator(false));

        var result = await service.IssueAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.ValidationFailed, result.State);
        Assert.NotEmpty(result.ValidationIssues!);
        var stored = await StoredAsync(f, f.Revision.Id);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, stored.Status);
        Assert.Null(stored.IssuedUtc);
        Assert.Empty(await HistoryAsync(f));
    }

    [Theory]
    [InlineData(AssessmentReportRevisionStatus.Draft)]
    [InlineData(AssessmentReportRevisionStatus.InReview)]
    [InlineData(AssessmentReportRevisionStatus.Rejected)]
    public async Task Issue_From_Invalid_Status_Is_Rejected_Without_Changes(AssessmentReportRevisionStatus status)
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        await RevisionStatusSeed.SetAsync(f.Db, f.Revision, status);
        var validator = new StubValidator(true);
        var service = Create(f, validator);

        var result = await service.IssueAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        Assert.Empty(validator.Stages);
        Assert.Equal(status, (await StoredAsync(f, f.Revision.Id)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Stale_Stamp_Returns_ConcurrencyConflict_Before_Policy_Evaluation()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var service = Create(f);

        var result = await service.IssueAsync(f.Revision.Id, Guid.NewGuid(), Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Duplicate_Issuance_Does_Not_Create_Duplicate_History()
    {
        await using var f = await ApprovedAsync();
        var service = Create(f);
        var stamp = f.Revision.ConcurrencyStamp;

        var first = await service.IssueAsync(f.Revision.Id, stamp, Caller("author-1"));
        var staleRetry = await service.IssueAsync(f.Revision.Id, stamp, Caller("author-1"));
        var currentRetry = await service.IssueAsync(f.Revision.Id, first.ConcurrencyStamp!.Value, Caller("author-1"));

        Assert.True(first.Succeeded);
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, staleRetry.State);
        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, currentRetry.State);
        Assert.Single(await HistoryAsync(f));
    }

    [Fact]
    public async Task Older_Approved_Revision_Cannot_Replace_Higher_Numbered_Issued_Revision()
    {
        await using var f = await ApprovedAsync();
        var now = DateTime.UtcNow;
        var existing = new AssessmentReportRevision
        {
            Id = Guid.NewGuid(),
            AssessmentReportId = f.Report.Id,
            RevisionNumber = f.Revision.RevisionNumber + 1,
            Status = AssessmentReportRevisionStatus.Issued,
            SubmittedForReviewUtc = now,
            SubmittedForReviewByUserId = "seed",
            ApprovedUtc = now,
            ApprovedByUserId = "seed",
            IssuedUtc = now,
            IssuedByUserId = "seed",
            CreatedUtc = now,
            UpdatedUtc = now,
            CreatedByUserId = "seed",
            UpdatedByUserId = "seed"
        };
        f.Db.Set<AssessmentReportRevision>().Add(existing);
        await f.Db.SaveChangesAsync();
        var service = Create(f);

        var result = await service.IssueAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, f.Revision.Id)).Status);
        var other = await StoredAsync(f, existing.Id);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, other.Status);
        Assert.Null(other.SupersededByRevisionId);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Concurrent_Change_Detected_At_Save_Returns_ConcurrencyConflict_And_Writes_Nothing()
    {
        await using var f = await ApprovedAsync();
        var service = Create(f);
        var stamp = f.Revision.ConcurrencyStamp;
        var newStamp = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options;
        await using (var other = new ApplicationDbContext(options))
        {
            await other.Set<AssessmentReportRevision>().Where(r => r.Id == f.Revision.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConcurrencyStamp, newStamp));
        }

        var result = await service.IssueAsync(f.Revision.Id, stamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        Assert.Equal(newStamp, result.ConcurrencyStamp);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, f.Revision.Id)).Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task History_Insert_Failure_Rolls_Back_Issuance()
    {
        await using var f = await ApprovedAsync();
        var table = f.Db.Model.FindEntityType(typeof(AssessmentReportTransitionHistory))!.GetTableName();
#pragma warning disable EF1002
        await f.Db.Database.ExecuteSqlRawAsync(
            $"CREATE TRIGGER fail_history BEFORE INSERT ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'blocked'); END;");
#pragma warning restore EF1002
        var service = Create(f);
        var id = f.Revision.Id;

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.IssueAsync(id, f.Revision.ConcurrencyStamp, Caller("author-1")));

        var stored = await StoredAsync(f, id);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, stored.Status);
        Assert.Null(stored.IssuedUtc);
        Assert.Null(stored.IssuedByUserId);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Cancellation_Before_Save_Writes_Nothing()
    {
        await using var f = await ApprovedAsync();
        var service = Create(f);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.IssueAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"), cts.Token));

        Assert.Equal(AssessmentReportRevisionStatus.Approved, (await StoredAsync(f, f.Revision.Id)).Status);
        Assert.Empty(await HistoryAsync(f));
    }
}
