using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using Xunit;
using Fixture = SentinelMfgSuite.Web.Tests.AssessmentReportRevisionContentServiceTests.Fixture;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentReportLifecycleServiceTests
{
    private static readonly DateTimeOffset T1 = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(1);
    private static readonly DateTimeOffset T3 = T1.AddHours(2);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
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

    private static AssessmentReportLifecycleService Create(
        Fixture f, TimeProvider time, IAssessmentReportContentValidator? validator = null)
        => new(f.Db, validator ?? new StubValidator(true), time);

    private static ClaimsPrincipal Caller(string? id, string? role = "AssessmentAdmin", bool authenticated = true)
    {
        var claims = new List<Claim>();
        if (id is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(authenticated ? new ClaimsIdentity(claims, "test") : new ClaimsIdentity(claims));
    }

    private static async Task<List<AssessmentReportTransitionHistory>> HistoryAsync(Fixture f)
        => await f.Db.Set<AssessmentReportTransitionHistory>().AsNoTracking()
            .Where(h => h.AssessmentReportRevisionId == f.Revision.Id)
            .OrderBy(h => h.OccurredUtc).ToListAsync();

    [Fact]
    public async Task Submit_Approve_Sets_Status_Audit_Stamps_And_History()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var time = new ManualTimeProvider(T1);
        var validator = new StubValidator(true);
        var service = Create(f, time, validator);
        var snapshot = f.Revision.ScoringSnapshot;

        var submit = await service.SubmitForReviewAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));
        time.Now = T2;
        var approve = await service.ApproveAsync(f.Revision.Id, submit.ConcurrencyStamp!.Value, Caller("author-1"));

        Assert.True(submit.Succeeded);
        Assert.True(approve.Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, f.Revision.Status);
        Assert.Equal(T1.UtcDateTime, f.Revision.SubmittedForReviewUtc);
        Assert.Equal("author-1", f.Revision.SubmittedForReviewByUserId);
        Assert.Equal(T2.UtcDateTime, f.Revision.ApprovedUtc);
        Assert.Equal("author-1", f.Revision.ApprovedByUserId);
        Assert.Equal(snapshot, f.Revision.ScoringSnapshot);
        Assert.Equal(new[] { AssessmentReportContentStage.InReview, AssessmentReportContentStage.Approved }, validator.Stages);

        var history = await HistoryAsync(f);
        Assert.Equal(2, history.Count);
        Assert.All(history, h =>
        {
            Assert.Equal(AssessmentReportTransitionEventType.Transition, h.EventType);
            Assert.Equal(f.Report.Id, h.AssessmentReportId);
            Assert.Equal("author-1", h.ActorUserId);
            Assert.Null(h.Reason);
            Assert.Null(h.ReplacedRevisionId);
            Assert.Null(h.ReplacementRevisionId);
            Assert.False(string.IsNullOrWhiteSpace(h.CorrelationId));
        });
        Assert.Equal(AssessmentReportRevisionStatus.Draft, history[0].PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.InReview, history[0].NewStatus);
        Assert.Equal(T1.UtcDateTime, history[0].OccurredUtc);
        Assert.Equal(AssessmentReportRevisionStatus.InReview, history[1].PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, history[1].NewStatus);
        Assert.NotEqual(history[0].CorrelationId, history[1].CorrelationId);
    }

    [Theory]
    [InlineData(AssessmentReportRevisionStatus.InReview)]
    [InlineData(AssessmentReportRevisionStatus.Approved)]
    public async Task Reject_From_InReview_Or_Approved_Preserves_Prior_Stamps_And_Stores_Trimmed_Reason(
        AssessmentReportRevisionStatus from)
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var time = new ManualTimeProvider(T1);
        var service = Create(f, time);
        var stamp = f.Revision.ConcurrencyStamp;
        var r = await service.SubmitForReviewAsync(f.Revision.Id, stamp, Caller("author-1"));
        stamp = r.ConcurrencyStamp!.Value;
        if (from == AssessmentReportRevisionStatus.Approved)
        {
            time.Now = T2;
            stamp = (await service.ApproveAsync(f.Revision.Id, stamp, Caller("author-1"))).ConcurrencyStamp!.Value;
        }
        time.Now = T3;

        var rejected = await service.RejectAsync(f.Revision.Id, stamp, "  needs work  ", Caller("author-1"));

        Assert.True(rejected.Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Rejected, f.Revision.Status);
        Assert.Equal(T1.UtcDateTime, f.Revision.SubmittedForReviewUtc);
        Assert.Equal(from == AssessmentReportRevisionStatus.Approved ? T2.UtcDateTime : null, f.Revision.ApprovedUtc);
        var last = (await HistoryAsync(f)).Last();
        Assert.Equal(from, last.PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.Rejected, last.NewStatus);
        Assert.Equal("needs work", last.Reason);
        Assert.Equal(T3.UtcDateTime, last.OccurredUtc);
    }

    [Fact]
    public async Task Reject_Does_Not_Require_Content_To_Pass_Validation()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        f.Revision.Status = AssessmentReportRevisionStatus.InReview;
        await f.Db.SaveChangesAsync();
        var validator = new StubValidator(false);
        var service = Create(f, new ManualTimeProvider(T1), validator);

        var result = await service.RejectAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, "no", Caller("author-1"));

        Assert.True(result.Succeeded);
        Assert.Empty(validator.Stages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Reject_Requires_Reason(string? reason)
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        f.Revision.Status = AssessmentReportRevisionStatus.InReview;
        await f.Db.SaveChangesAsync();
        var service = Create(f, new ManualTimeProvider(T1));

        var result = await service.RejectAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, reason, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.InvalidReason, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.InReview, f.Revision.Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Reject_Reason_Length_Boundary()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        f.Revision.Status = AssessmentReportRevisionStatus.InReview;
        await f.Db.SaveChangesAsync();
        var service = Create(f, new ManualTimeProvider(T1));

        var tooLong = await service.RejectAsync(f.Revision.Id, f.Revision.ConcurrencyStamp,
            new string('a', AssessmentReportTransitionPolicy.MaxReasonLength + 1), Caller("author-1"));
        var max = await service.RejectAsync(f.Revision.Id, f.Revision.ConcurrencyStamp,
            " " + new string('a', AssessmentReportTransitionPolicy.MaxReasonLength) + " ", Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.InvalidReason, tooLong.State);
        Assert.True(max.Succeeded);
    }

    [Fact]
    public async Task Invalid_Transitions_Are_Rejected_Without_Changes()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var service = Create(f, new ManualTimeProvider(T1));
        var stamp = f.Revision.ConcurrencyStamp;

        var approveDraft = await service.ApproveAsync(f.Revision.Id, stamp, Caller("author-1"));
        var rejectDraft = await service.RejectAsync(f.Revision.Id, stamp, "reason", Caller("author-1"));
        f.Revision.Status = AssessmentReportRevisionStatus.Rejected;
        await f.Db.SaveChangesAsync();
        var submitRejected = await service.SubmitForReviewAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, approveDraft.State);
        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, rejectDraft.State);
        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, submitRejected.State);
        Assert.Empty(await HistoryAsync(f));
        Assert.Null(f.Revision.SubmittedForReviewUtc);
    }

    [Theory]
    [InlineData(AssessmentReportRevisionStatus.InReview)]
    [InlineData(AssessmentReportRevisionStatus.Approved)]
    [InlineData(AssessmentReportRevisionStatus.Issued)]
    public async Task Submit_Rejects_NonDraft_And_Duplicate_Requests(AssessmentReportRevisionStatus status)
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        f.Revision.Status = status;
        await f.Db.SaveChangesAsync();
        var service = Create(f, new ManualTimeProvider(T1));

        var result = await service.SubmitForReviewAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, result.State);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Retry_With_Old_Stamp_After_Success_Returns_ConcurrencyConflict_Without_Duplicate_History()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var service = Create(f, new ManualTimeProvider(T1));
        var stamp = f.Revision.ConcurrencyStamp;

        var submit = await service.SubmitForReviewAsync(f.Revision.Id, stamp, Caller("author-1"));
        Assert.True(submit.Succeeded);
        var retry = await service.SubmitForReviewAsync(f.Revision.Id, stamp, Caller("author-1"));
        var approve = await service.ApproveAsync(f.Revision.Id, submit.ConcurrencyStamp!.Value, Caller("author-1"));
        var staleAfterApprove = await service.ApproveAsync(f.Revision.Id, submit.ConcurrencyStamp!.Value, Caller("author-1"));
        var reject = await service.RejectAsync(f.Revision.Id, approve.ConcurrencyStamp!.Value, "r", Caller("author-1"));
        var staleAfterReject = await service.RejectAsync(f.Revision.Id, approve.ConcurrencyStamp!.Value, "r", Caller("author-1"));
        var currentInvalid = await service.SubmitForReviewAsync(f.Revision.Id, reject.ConcurrencyStamp!.Value, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, retry.State);
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, staleAfterApprove.State);
        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, staleAfterReject.State);
        Assert.Equal(AssessmentReportLifecycleState.InvalidTransition, currentInvalid.State);
        Assert.Equal(3, (await HistoryAsync(f)).Count);
    }

    [Fact]
    public async Task Stale_Stamp_Returns_ConcurrencyConflict()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var service = Create(f, new ManualTimeProvider(T1));

        var result = await service.SubmitForReviewAsync(f.Revision.Id, Guid.NewGuid(), Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, f.Revision.Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Concurrent_Change_Detected_At_Save_Returns_ConcurrencyConflict_And_Writes_Nothing()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var service = Create(f, new ManualTimeProvider(T1));
        var stamp = f.Revision.ConcurrencyStamp;
        var newStamp = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options;
        await using (var other = new ApplicationDbContext(options))
        {
            await other.Set<AssessmentReportRevision>().Where(r => r.Id == f.Revision.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConcurrencyStamp, newStamp));
        }

        var result = await service.SubmitForReviewAsync(f.Revision.Id, stamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.ConcurrencyConflict, result.State);
        Assert.Equal(newStamp, result.ConcurrencyStamp);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task History_Insert_Failure_Rolls_Back_Status_Change()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var table = f.Db.Model.FindEntityType(typeof(AssessmentReportTransitionHistory))!.GetTableName();
#pragma warning disable EF1002
        await f.Db.Database.ExecuteSqlRawAsync(
            $"CREATE TRIGGER fail_history BEFORE INSERT ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'blocked'); END;");
#pragma warning restore EF1002
        var service = Create(f, new ManualTimeProvider(T1));
        var id = f.Revision.Id;

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.SubmitForReviewAsync(id, f.Revision.ConcurrencyStamp, Caller("author-1")));

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.Db.Database.GetConnectionString()!).Options;
        await using var fresh = new ApplicationDbContext(options);
        var stored = await fresh.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, stored.Status);
        Assert.Null(stored.SubmittedForReviewUtc);
    }

    [Fact]
    public async Task Authorization_And_Ownership()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var service = Create(f, new ManualTimeProvider(T1));
        var stamp = f.Revision.ConcurrencyStamp;

        Assert.Equal(AssessmentReportLifecycleState.Forbidden,
            (await service.SubmitForReviewAsync(f.Revision.Id, stamp, Caller("author-1", authenticated: false))).State);
        Assert.Equal(AssessmentReportLifecycleState.Forbidden,
            (await service.SubmitForReviewAsync(f.Revision.Id, stamp, Caller("author-1", role: null))).State);
        Assert.Equal(AssessmentReportLifecycleState.Forbidden,
            (await service.ApproveAsync(f.Revision.Id, stamp, Caller(null))).State);
        Assert.Equal(AssessmentReportLifecycleState.Forbidden,
            (await service.RejectAsync(f.Revision.Id, stamp, "r", Caller(" "))).State);
        Assert.Equal(AssessmentReportLifecycleState.NotFound,
            (await service.SubmitForReviewAsync(Guid.NewGuid(), stamp, Caller("author-1"))).State);
        Assert.Equal(AssessmentReportLifecycleState.NotFound,
            (await service.SubmitForReviewAsync(f.Revision.Id, stamp, Caller("someone-else"))).State);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, f.Revision.Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Null_Owner_Allows_Any_Assessment_Admin_And_Records_Actual_Actor()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var assessment = await f.Db.Set<Assessment>().SingleAsync(a => a.Id == f.Report.AssessmentId);
        assessment.OwnerUserId = null;
        await f.Db.SaveChangesAsync();
        var service = Create(f, new ManualTimeProvider(T1));

        var result = await service.SubmitForReviewAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("someone-else"));

        Assert.True(result.Succeeded);
        Assert.Equal("someone-else", (await HistoryAsync(f)).Single().ActorUserId);
        Assert.Equal("someone-else", f.Revision.SubmittedForReviewByUserId);
    }

    [Fact]
    public async Task Validation_Failure_With_Real_Validator_Writes_Nothing()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        var service = Create(f, new ManualTimeProvider(T1), new AssessmentReportContentValidator());

        var result = await service.SubmitForReviewAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.ValidationFailed, result.State);
        Assert.NotEmpty(result.ValidationIssues!);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, f.Revision.Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Missing_Or_Inconsistent_Source_Data_Returns_InvalidSourceData()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        f.Revision.ScoringSnapshot = null;
        await f.Db.SaveChangesAsync();
        var service = Create(f, new ManualTimeProvider(T1));

        var result = await service.SubmitForReviewAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.InvalidSourceData, result.State);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, f.Revision.Status);
        Assert.Empty(await HistoryAsync(f));
    }

    [Fact]
    public async Task Unsupported_Persisted_Content_Schema_Returns_InvalidSourceData()
    {
        await using var f = await Fixture.CreateAsync("3.0", true);
        f.Revision.ConsultantContentJson = "{}";
        f.Revision.ConsultantContentSchemaVersion = "0.1";
        await f.Db.SaveChangesAsync();
        var service = Create(f, new ManualTimeProvider(T1));

        var result = await service.SubmitForReviewAsync(f.Revision.Id, f.Revision.ConcurrencyStamp, Caller("author-1"));

        Assert.Equal(AssessmentReportLifecycleState.InvalidSourceData, result.State);
    }
}
