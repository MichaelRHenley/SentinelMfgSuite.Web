using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports;

public sealed class AssessmentReportRevisionContentService : IAssessmentReportRevisionContentService
{
    private readonly ApplicationDbContext _db;
    private readonly IAssessmentReportContentValidator _validator;

    public AssessmentReportRevisionContentService(
        ApplicationDbContext db,
        IAssessmentReportContentValidator validator)
    {
        _db = db;
        _validator = validator;
    }

    public async Task<AssessmentReportRevisionContentResult> GetAsync(Guid revisionId, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        if (!TryGetCaller(caller, out var callerId))
        {
            return new(AssessmentReportRevisionContentState.Forbidden);
        }
        var revision = await LoadOwnedRevisionAsync(revisionId, callerId, tracking: false, cancellationToken);
        if (revision is null)
        {
            return new(AssessmentReportRevisionContentState.NotFound);
        }
        if (revision.ConsultantContentJson is null)
        {
            if (revision.ConsultantContentSchemaVersion is not null)
            {
                throw new JsonException("Consultant content schema version is set but revision content is empty.");
            }
            return new(AssessmentReportRevisionContentState.Retrieved, ConsultantReportContent.CreateEmpty(), revision.ConcurrencyStamp, UpdatedUtc: revision.UpdatedUtc);
        }
        if (!string.Equals(revision.ConsultantContentSchemaVersion, ConsultantReportContent.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported consultant report content schema version '{revision.ConsultantContentSchemaVersion}'.");
        }

        var content = ConsultantReportContentSerializer.Deserialize(revision.ConsultantContentJson);
        if (!string.Equals(content.SchemaVersion, revision.ConsultantContentSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException("Consultant content schema version does not match its revision metadata.");
        }

        return new(AssessmentReportRevisionContentState.Retrieved, content, revision.ConcurrencyStamp, UpdatedUtc: revision.UpdatedUtc);
    }

    public async Task<AssessmentReportRevisionContentResult> SaveDraftAsync(
        Guid revisionId,
        Guid expectedConcurrencyStamp,
        ConsultantReportContent content,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!TryGetCaller(caller, out var userId))
        {
            return new(AssessmentReportRevisionContentState.Forbidden);
        }
        if (!string.Equals(content.SchemaVersion, ConsultantReportContent.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return new(AssessmentReportRevisionContentState.UnsupportedContentSchemaVersion);
        }

        string serializedContent;
        try
        {
            serializedContent = ConsultantReportContentSerializer.Serialize(content);
        }
        catch (JsonException)
        {
            return new(AssessmentReportRevisionContentState.InvalidSourceData);
        }

        var revision = await LoadOwnedRevisionAsync(revisionId, userId, tracking: true, cancellationToken);
        if (revision is null)
        {
            return new(AssessmentReportRevisionContentState.NotFound);
        }
        if (revision.Status != AssessmentReportRevisionStatus.Draft)
        {
            return new(AssessmentReportRevisionContentState.NotDraft, ConcurrencyStamp: revision.ConcurrencyStamp);
        }
        if (revision.ConcurrencyStamp != expectedConcurrencyStamp)
        {
            return new(AssessmentReportRevisionContentState.ConcurrencyConflict, ConcurrencyStamp: revision.ConcurrencyStamp);
        }

        var report = revision.AssessmentReport;
        if (report is null)
        {
            return new(AssessmentReportRevisionContentState.InvalidSourceData);
        }

        var validationContext = await AssessmentReportValidationContextBuilder.BuildAsync(
            _db, revision, AssessmentReportContentStage.Draft, cancellationToken);
        if (validationContext is null)
        {
            return new(AssessmentReportRevisionContentState.InvalidSourceData);
        }

        var validation = _validator.Validate(content, validationContext);
        if (!validation.IsValid)
        {
            return new(AssessmentReportRevisionContentState.ValidationFailed, content, revision.ConcurrencyStamp, validation.Issues, revision.UpdatedUtc);
        }

        revision.ConsultantContentJson = serializedContent;
        revision.ConsultantContentSchemaVersion = ConsultantReportContent.CurrentSchemaVersion;
        revision.UpdatedUtc = DateTime.UtcNow;
        revision.UpdatedByUserId = userId;
        revision.ConcurrencyStamp = Guid.NewGuid();
        _db.Entry(revision).Property(item => item.ConcurrencyStamp).OriginalValue = expectedConcurrencyStamp;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.Entry(revision).State = EntityState.Detached;
            var latestStamp = await _db.Set<AssessmentReportRevision>()
                .AsNoTracking()
                .Where(item => item.Id == revisionId)
                .Select(item => (Guid?)item.ConcurrencyStamp)
                .SingleOrDefaultAsync(cancellationToken);
            return new(AssessmentReportRevisionContentState.ConcurrencyConflict, ConcurrencyStamp: latestStamp);
        }

        return new(AssessmentReportRevisionContentState.Saved, content, revision.ConcurrencyStamp, validation.Issues, revision.UpdatedUtc);
    }

    private Task<AssessmentReportRevision?> LoadOwnedRevisionAsync(
        Guid revisionId,
        string callerId,
        bool tracking,
        CancellationToken cancellationToken)
        => AssessmentReportAccess.LoadOwnedRevisionAsync(_db, revisionId, callerId, tracking, cancellationToken);

    private static bool TryGetCaller(ClaimsPrincipal caller, out string callerId)
        => AssessmentReportAccess.TryGetCaller(caller, out callerId);
}
