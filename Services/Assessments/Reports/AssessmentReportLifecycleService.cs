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

    public Task<AssessmentReportLifecycleResult> IssueAsync(
        Guid revisionId, Guid expectedConcurrencyStamp, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
        => TransitionAsync(revisionId, expectedConcurrencyStamp, AssessmentReportRevisionStatus.Issued,
            null, AssessmentReportContentStage.Issued, caller, cancellationToken);

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

        // The predecessor is always derived from persisted state (the single other Issued revision of the same
        // report); callers cannot supply or override it.
        AssessmentReportRevision? predecessor = null;
        if (target == AssessmentReportRevisionStatus.Issued)
        {
            var reportId = revision.AssessmentReportId;
            var issued = await _db.Set<AssessmentReportRevision>()
                .Where(item => item.AssessmentReportId == reportId
                    && item.Id != revisionId
                    && item.Status == AssessmentReportRevisionStatus.Issued)
                .Take(2)
                .ToListAsync(cancellationToken);
            if (issued.Count > 1)
            {
                foreach (var item in issued)
                {
                    _db.Entry(item).State = EntityState.Detached;
                }
                _db.Entry(revision).State = EntityState.Detached;
                throw new InvalidOperationException(
                    $"Invariant violation: report {reportId} has more than one Issued revision besides {revisionId}.");
            }

            if (issued.Count == 1)
            {
                predecessor = issued[0];
                if (revision.RevisionNumber <= predecessor.RevisionNumber)
                {
                    _db.Entry(predecessor).State = EntityState.Detached;
                    return new(AssessmentReportLifecycleState.InvalidTransition, previous, revision.ConcurrencyStamp);
                }
            }
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (predecessor?.IssuedUtc is { } predecessorIssuedUtc && now < predecessorIssuedUtc)
        {
            // The database requires the replacement to be issued no earlier than the predecessor. The timestamp is
            // never adjusted; an earlier clock reading means the replacement cannot be issued now.
            _db.Entry(predecessor).State = EntityState.Detached;
            return new(AssessmentReportLifecycleState.InvalidTransition, previous, revision.ConcurrencyStamp);
        }

        void ApplyTransition()
        {
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
                case AssessmentReportRevisionStatus.Issued:
                    revision.IssuedUtc = now;
                    revision.IssuedByUserId = actorId;
                    break;
            }
            revision.ConcurrencyStamp = Guid.NewGuid();
            _db.Entry(revision).Property(item => item.ConcurrencyStamp).OriginalValue = expectedConcurrencyStamp;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var histories = new List<AssessmentReportTransitionHistory>();
        if (predecessor is not null)
        {
            histories.Add(new AssessmentReportTransitionHistory
            {
                Id = Guid.NewGuid(),
                AssessmentReportId = revision.AssessmentReportId,
                AssessmentReportRevisionId = predecessor.Id,
                EventType = AssessmentReportTransitionEventType.Transition,
                PreviousStatus = AssessmentReportRevisionStatus.Issued,
                NewStatus = AssessmentReportRevisionStatus.Superseded,
                ActorUserId = actorId,
                OccurredUtc = now,
                Reason = null,
                ReplacedRevisionId = null,
                ReplacementRevisionId = revision.Id,
                CorrelationId = correlationId
            });
        }
        histories.Add(new AssessmentReportTransitionHistory
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
            ReplacedRevisionId = predecessor?.Id,
            ReplacementRevisionId = null,
            CorrelationId = correlationId
        });

        void DetachTracked()
        {
            foreach (var item in histories)
            {
                _db.Entry(item).State = EntityState.Detached;
            }
            _db.Entry(revision).State = EntityState.Detached;
            if (predecessor is not null)
            {
                _db.Entry(predecessor).State = EntityState.Detached;
            }
        }

        // Each stamped UPDATE runs in its own save inside one transaction: it takes the row lock and serializes competing
        // callers, so stale callers fail on the concurrency token instead of colliding on unique indexes. EF does not
        // order the history INSERT after the revision UPDATE (no FK dependency), hence separate saves.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (predecessor is not null)
            {
                // Save 1: the predecessor must leave Issued before the replacement becomes Issued (the unique
                // current-Issued index is not deferrable). Its original stamp is the one loaded above.
                predecessor.Status = AssessmentReportRevisionStatus.Superseded;
                predecessor.SupersededByRevisionId = revision.Id;
                predecessor.UpdatedUtc = now;
                predecessor.UpdatedByUserId = actorId;
                predecessor.ConcurrencyStamp = Guid.NewGuid();
                await _db.SaveChangesAsync(cancellationToken);
            }

            // Save 2 (or 1 for non-replacement transitions): the stamped revision update.
            ApplyTransition();
            await _db.SaveChangesAsync(cancellationToken);

            // Final save: append the history event(s); the deferred supersession trigger validates the pair at commit.
            _db.Set<AssessmentReportTransitionHistory>().AddRange(histories);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            DetachTracked();
            var latestStamp = await _db.Set<AssessmentReportRevision>()
                .AsNoTracking()
                .Where(item => item.Id == revisionId)
                .Select(item => (Guid?)item.ConcurrencyStamp)
                .SingleOrDefaultAsync(cancellationToken);
            return new(AssessmentReportLifecycleState.ConcurrencyConflict, ConcurrencyStamp: latestStamp);
        }
        catch (DbUpdateException ex) when (predecessor is null && IsCurrentIssuedViolation(ex))
        {
            // Initial issuance lost a race with a concurrent issuance of a different revision of the same report.
            // Only this specific unique index is classified, and only after confirming that another Issued revision
            // is now persisted. In the replacement path the same violation is unexpected and is rethrown.
            await transaction.RollbackAsync(cancellationToken);
            DetachTracked();
            if (!await HasOtherIssuedRevisionAsync(revision, cancellationToken))
            {
                throw;
            }

            return new(AssessmentReportLifecycleState.InvalidTransition, previous);
        }
        catch
        {
            // The transaction is disposed (rolled back) on exit; drop tracked state that no longer matches the database.
            DetachTracked();
            throw;
        }

        return new(AssessmentReportLifecycleState.Succeeded, target, revision.ConcurrencyStamp,
            SupersededRevisionId: predecessor?.Id, SupersededRevisionStamp: predecessor?.ConcurrencyStamp);
    }

    private const string CurrentIssuedIndexName = "UX_AssessmentReportRevisions_Report_CurrentIssued";

    private static bool IsCurrentIssuedViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation
            && string.Equals(pg.ConstraintName, CurrentIssuedIndexName, StringComparison.Ordinal);

    private Task<bool> HasOtherIssuedRevisionAsync(AssessmentReportRevision revision, CancellationToken cancellationToken)
    {
        var reportId = revision.AssessmentReportId;
        var revisionId = revision.Id;
        return _db.Set<AssessmentReportRevision>().AsNoTracking().AnyAsync(
            item => item.AssessmentReportId == reportId
                && item.Id != revisionId
                && item.Status == AssessmentReportRevisionStatus.Issued,
            cancellationToken);
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
