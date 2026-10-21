using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports;

public sealed class AssessmentReportLifecycleService : IAssessmentReportLifecycleService
{
    private readonly ApplicationDbContext _db;
    private readonly IAssessmentReportContentValidator _validator;
    private readonly TimeProvider _timeProvider;

    public AssessmentReportLifecycleService(
        ApplicationDbContext db,
        IAssessmentReportContentValidator validator,
        TimeProvider timeProvider)
    {
        _db = db;
        _validator = validator;
        _timeProvider = timeProvider;
    }

    public Task<AssessmentReportLifecycleResult> SubmitForReviewAsync(
        Guid revisionId, Guid expectedConcurrencyStamp, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
        => TransitionAsync(revisionId, expectedConcurrencyStamp, AssessmentReportRevisionStatus.InReview,
            null, AssessmentReportContentStage.InReview, caller, cancellationToken);

    public Task<AssessmentReportLifecycleResult> ApproveAsync(
        Guid revisionId, Guid expectedConcurrencyStamp, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
        => TransitionAsync(revisionId, expectedConcurrencyStamp, AssessmentReportRevisionStatus.Approved,
            null, AssessmentReportContentStage.Approved, caller, cancellationToken);

    public Task<AssessmentReportLifecycleResult> RejectAsync(
        Guid revisionId, Guid expectedConcurrencyStamp, string? reason, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
        => TransitionAsync(revisionId, expectedConcurrencyStamp, AssessmentReportRevisionStatus.Rejected,
            reason, null, caller, cancellationToken);

    private async Task<AssessmentReportLifecycleResult> TransitionAsync(
        Guid revisionId,
        Guid expectedConcurrencyStamp,
        AssessmentReportRevisionStatus target,
        string? reason,
        AssessmentReportContentStage? validationStage,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        if (!AssessmentReportAccess.TryGetCaller(caller, out var actorId))
        {
            return new(AssessmentReportLifecycleState.Forbidden);
        }

        var revision = await AssessmentReportAccess.LoadOwnedRevisionAsync(
            _db, revisionId, actorId, tracking: true, cancellationToken);
        if (revision is null)
        {
            return new(AssessmentReportLifecycleState.NotFound);
        }

        var previous = revision.Status;
        if (revision.ConcurrencyStamp != expectedConcurrencyStamp)
        {
            return new(AssessmentReportLifecycleState.ConcurrencyConflict, previous, revision.ConcurrencyStamp);
        }

        var evaluation = AssessmentReportTransitionPolicy.Evaluate(previous, target, reason);
        if (!evaluation.Succeeded)
        {
            var state = evaluation.Failure == AssessmentReportTransitionFailure.InvalidReason
                ? AssessmentReportLifecycleState.InvalidReason
                : AssessmentReportLifecycleState.InvalidTransition;
            return new(state, previous, revision.ConcurrencyStamp);
        }

        if (validationStage is { } stage)
        {
            ConsultantReportContent content;
            try
            {
                content = ReadContent(revision);
            }
            catch (JsonException)
            {
                return new(AssessmentReportLifecycleState.InvalidSourceData, previous, revision.ConcurrencyStamp);
            }

            var context = await AssessmentReportValidationContextBuilder.BuildAsync(_db, revision, stage, cancellationToken);
            if (context is null)
            {
                return new(AssessmentReportLifecycleState.InvalidSourceData, previous, revision.ConcurrencyStamp);
            }

            var validation = _validator.Validate(content, context);
            if (!validation.IsValid)
            {
                return new(AssessmentReportLifecycleState.ValidationFailed, previous, revision.ConcurrencyStamp, validation.Issues);
            }
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        revision.Status = target;
        revision.UpdatedUtc = now;
        revision.UpdatedByUserId = actorId;
        switch (target)
        {
            case AssessmentReportRevisionStatus.InReview:
                revision.SubmittedForReviewUtc = now;
                revision.SubmittedForReviewByUserId = actorId;
                break;
            case AssessmentReportRevisionStatus.Approved:
                revision.ApprovedUtc = now;
                revision.ApprovedByUserId = actorId;
                break;
        }
        revision.ConcurrencyStamp = Guid.NewGuid();
        _db.Entry(revision).Property(item => item.ConcurrencyStamp).OriginalValue = expectedConcurrencyStamp;

        var history = new AssessmentReportTransitionHistory
        {
            Id = Guid.NewGuid(),
            AssessmentReportId = revision.AssessmentReportId,
            AssessmentReportRevisionId = revision.Id,
            EventType = AssessmentReportTransitionEventType.Transition,
            PreviousStatus = previous,
            NewStatus = target,
            ActorUserId = actorId,
            OccurredUtc = now,
            Reason = evaluation.NormalizedReason,
            ReplacedRevisionId = null,
            ReplacementRevisionId = null,
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        _db.Set<AssessmentReportTransitionHistory>().Add(history);

        try
        {
            // A single SaveChanges commits the revision update and the history insert in one transaction.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.Entry(history).State = EntityState.Detached;
            _db.Entry(revision).State = EntityState.Detached;
            var latestStamp = await _db.Set<AssessmentReportRevision>()
                .AsNoTracking()
                .Where(item => item.Id == revisionId)
                .Select(item => (Guid?)item.ConcurrencyStamp)
                .SingleOrDefaultAsync(cancellationToken);
            return new(AssessmentReportLifecycleState.ConcurrencyConflict, ConcurrencyStamp: latestStamp);
        }

        return new(AssessmentReportLifecycleState.Succeeded, target, revision.ConcurrencyStamp);
    }

    private static ConsultantReportContent ReadContent(AssessmentReportRevision revision)
    {
        if (revision.ConsultantContentJson is null)
        {
            if (revision.ConsultantContentSchemaVersion is not null)
            {
                throw new JsonException("Consultant content schema version is set but revision content is empty.");
            }
            return ConsultantReportContent.CreateEmpty();
        }
        if (!string.Equals(revision.ConsultantContentSchemaVersion, ConsultantReportContent.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException("Unsupported consultant report content schema version.");
        }
        var content = ConsultantReportContentSerializer.Deserialize(revision.ConsultantContentJson);
        if (!string.Equals(content.SchemaVersion, revision.ConsultantContentSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException("Consultant content schema version does not match its revision metadata.");
        }
        return content;
    }
}
