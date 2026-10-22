using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed class AssessmentReportService : IAssessmentReportService
{
    private readonly ApplicationDbContext _db;
    private readonly IReadinessScoringService _scoringService;

    public AssessmentReportService(ApplicationDbContext db, IReadinessScoringService scoringService)
    {
        _db = db;
        _scoringService = scoringService;
    }

    public async Task<AssessmentReportServiceResult> CreateAsync(
        Guid assessmentId,
        Guid submittedResponseId,
        ClaimsPrincipal caller,
        CancellationToken ct = default)
    {
        if (!IsAssessmentAdmin(caller, out var callerUserId))
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.Forbidden);
        }
        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == assessmentId, ct);
        if (assessment is null)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.AssessmentNotFound);
        }

        if (!IsOwnerAllowed(assessment, callerUserId))
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.Forbidden);
        }

        var existing = await FindBySubmittedResponseIdAsync(submittedResponseId, ct);
        if (existing is not null)
        {
            return existing.AssessmentId == assessmentId
                ? AssessmentReportServiceResult.AlreadyExists(existing)
                : AssessmentReportServiceResult.Failure(AssessmentReportServiceState.ResponseAssessmentMismatch);
        }

        var response = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == submittedResponseId, ct);
        if (response is null)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.ResponseNotFound);
        }

        if (response.AssessmentId != assessmentId)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.ResponseAssessmentMismatch);
        }

        if (response.IsDraft || response.SubmittedUtc is null)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.ResponseNotSubmitted);
        }

        QuestionnaireDefinition definition;
        try
        {
            definition = QuestionnaireDefinitionResolver.Resolve(assessment.QuestionnaireVersion);
        }
        catch (ArgumentException)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.UnsupportedQuestionnaireVersion);
        }
        catch (KeyNotFoundException)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.UnsupportedQuestionnaireVersion);
        }

        QuestionnaireResponseV1? questionnaireResponse;
        try
        {
            questionnaireResponse = QuestionnaireResponseV1Serializer.Deserialize(response.Data);
        }
        catch (JsonException)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.InvalidResponse);
        }
        catch (NotSupportedException)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.InvalidResponse);
        }
        if (questionnaireResponse is null)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.InvalidResponse);
        }

        var validation = QuestionnaireResponseV1Validator.Validate(
            questionnaireResponse,
            definition,
            QuestionnaireValidationMode.Submission);
        if (!validation.IsValid)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.InvalidResponse);
        }

        var scoring = _scoringService.Score(assessment.QuestionnaireVersion, questionnaireResponse);
        var scoringSnapshot = ReadinessScoringSnapshotSerializer.Serialize(scoring);
        var generatedUtc = DateTime.UtcNow;
        var revisionId = Guid.NewGuid();
        var report = new AssessmentReport
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessmentId,
            SubmittedResponseId = response.Id,
            QuestionnaireVersion = assessment.QuestionnaireVersion,
            ResponseSubmittedUtc = response.SubmittedUtc.Value,
            CreatedUtc = generatedUtc,
            UpdatedUtc = generatedUtc,
            CreatedByUserId = callerUserId,
            UpdatedByUserId = callerUserId,
            Revisions =
            {
                new AssessmentReportRevision
                {
                    Id = revisionId,
                    RevisionNumber = 1,
                    Status = AssessmentReportRevisionStatus.Draft,
                    ExecutiveSummary = string.Empty,
                    ScopeAndMethodology = string.Empty,
                    AssumptionsAndLimitations = string.Empty,
                    NextSteps = string.Empty,
                    ScoringSnapshotSchemaVersion = ReadinessScoringSnapshotSerializer.PersistedSchemaVersion,
                    ScoringSnapshot = scoringSnapshot,
                    ReportTemplateVersion = SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.ReportTemplateVersions.Current,
                    CreatedUtc = generatedUtc,
                    UpdatedUtc = generatedUtc,
                    CreatedByUserId = callerUserId,
                    UpdatedByUserId = callerUserId
                }
            }
        };

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.Set<AssessmentReport>().Add(report);
        _db.Set<AssessmentReportTransitionHistory>().Add(new AssessmentReportTransitionHistory
        {
            Id = Guid.NewGuid(),
            AssessmentReportId = report.Id,
            AssessmentReportRevisionId = revisionId,
            EventType = AssessmentReportTransitionEventType.Created,
            PreviousStatus = null,
            NewStatus = AssessmentReportRevisionStatus.Draft,
            ActorUserId = callerUserId,
            OccurredUtc = generatedUtc,
            CorrelationId = Guid.NewGuid().ToString("N")
        });
        try
        {
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return AssessmentReportServiceResult.Created(report);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            var winner = await FindBySubmittedResponseIdAsync(submittedResponseId, ct);
            if (winner is not null && winner.AssessmentId == assessmentId)
            {
                return AssessmentReportServiceResult.AlreadyExists(winner);
            }

            throw;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public Task<AssessmentReportServiceResult> GetByIdAsync(
        Guid reportId,
        ClaimsPrincipal caller,
        CancellationToken ct = default)
        => GetAsync(query => query.Where(report => report.Id == reportId), caller, ct);

    public Task<AssessmentReportServiceResult> GetBySubmittedResponseIdAsync(
        Guid submittedResponseId,
        ClaimsPrincipal caller,
        CancellationToken ct = default)
        => GetAsync(query => query.Where(report => report.SubmittedResponseId == submittedResponseId), caller, ct);

    private async Task<AssessmentReportServiceResult> GetAsync(
        Func<IQueryable<AssessmentReport>, IQueryable<AssessmentReport>> filter,
        ClaimsPrincipal caller,
        CancellationToken ct)
    {
        if (!IsAssessmentAdmin(caller, out var callerUserId))
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.Forbidden);
        }

        var query = filter(_db.Set<AssessmentReport>()
            .AsNoTracking()
            .Include(report => report.Revisions)
            .Include(report => report.Assessment));
        var report = await query.SingleOrDefaultAsync(ct);
        if (report is null)
        {
            return AssessmentReportServiceResult.Failure(AssessmentReportServiceState.NotFound);
        }

        return IsOwnerAllowed(report.Assessment, callerUserId)
            ? AssessmentReportServiceResult.Retrieved(report)
            : AssessmentReportServiceResult.Failure(AssessmentReportServiceState.NotFound);
    }

    private Task<AssessmentReport?> FindBySubmittedResponseIdAsync(Guid submittedResponseId, CancellationToken ct)
        => _db.Set<AssessmentReport>()
            .AsNoTracking()
            .Include(report => report.Revisions)
            .SingleOrDefaultAsync(report => report.SubmittedResponseId == submittedResponseId, ct);

    private static bool IsAssessmentAdmin(ClaimsPrincipal caller, out string callerUserId)
        => AssessmentReportAccess.TryGetCaller(caller, out callerUserId);

    private static bool IsOwnerAllowed(Assessment assessment, string callerUserId)
        => AssessmentReportAccess.IsOwnerAllowed(assessment, callerUserId);
}
