using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Controllers;

[Authorize(Policy = "SentinelAssessmentAdmin")]
public class InternalAssessmentReviewController : Controller
{
    private const string QuestionnaireViewPath = "~/Views/AssessmentQuestionnaire/Questionnaire.cshtml";
    private const string UnavailableViewPath = "~/Views/InternalAssessmentReview/Unavailable.cshtml";

    private readonly ApplicationDbContext _db;
    private readonly IReadinessScoringService _readinessScoringService;

    public InternalAssessmentReviewController(ApplicationDbContext db, IReadinessScoringService readinessScoringService)
    {
        _db = db;
        _readinessScoringService = readinessScoringService;
    }

    [HttpGet("/Internal/Assessments/{assessmentId:guid}/ReviewResponses")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> ReviewResponses(Guid assessmentId, CancellationToken ct = default)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == assessmentId, ct);

        if (assessment is null)
        {
            return View(UnavailableViewPath, CreateUnavailableModel("This assessment could not be found."));
        }

        QuestionnaireDefinition definition;
        try
        {
            definition = QuestionnaireDefinitionResolver.Resolve(assessment.QuestionnaireVersion);
        }
        catch (ArgumentException)
        {
            return View(UnavailableViewPath, CreateUnavailableModel("This questionnaire version is not available for review."));
        }

        var response = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .Where(r => r.AssessmentId == assessmentId && !r.IsDraft && r.SubmittedUtc != null)
            .OrderByDescending(r => r.SubmittedUtc)
            .FirstOrDefaultAsync(ct);

        if (response is null)
        {
            return View(UnavailableViewPath, CreateUnavailableModel("This assessment does not have a submitted questionnaire response yet."));
        }

        QuestionnaireResponseV1? deserialized;
        try
        {
            deserialized = QuestionnaireResponseV1Serializer.Deserialize(response.Data);
        }
        catch
        {
            deserialized = null;
        }

        if (deserialized is null)
        {
            return View(UnavailableViewPath, CreateUnavailableModel("The recorded questionnaire data could not be read. Please contact engineering before relying on this response."));
        }

        if (!string.Equals(deserialized.SchemaVersion, definition.SchemaVersion, StringComparison.Ordinal))
        {
            return View(UnavailableViewPath, CreateUnavailableModel("The recorded questionnaire version does not match this assessment. Please contact engineering before relying on this response."));
        }

        var scoring = _readinessScoringService.Score(assessment.QuestionnaireVersion, deserialized);
        var scoringEvidenceLabels = definition.Sections
            .SelectMany(section => section.Questions)
            .SelectMany(question => (question.RepeatableFields ?? Array.Empty<FieldDefinition>())
                .Select(field => new KeyValuePair<string, string>(
                    $"{question.Id}.{field.Id}",
                    $"{question.DisplayNumber} — {field.Label}")))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var question in definition.Sections.SelectMany(section => section.Questions))
        {
            scoringEvidenceLabels[question.Id] = $"{question.DisplayNumber} — {question.Text}";
        }

        var sanitized = QuestionnaireResponseV1Sanitizer.SanitizeForClient(definition, deserialized);

        var q06Choices = definition.Sections
            .SelectMany(section => section.Questions)
            .Single(question => string.Equals(question.Id, "q06", StringComparison.Ordinal))
            .Choices ?? new List<Choice>();

        var priorityChoices = q06Choices
            .ToDictionary(choice => choice.Value, choice => choice.Label, StringComparer.Ordinal);

        var model = new AssessmentQuestionnairePageViewModel
        {
            InvitationId = Guid.Empty,
            InvitationToken = string.Empty,
            StatusLabel = "Submitted",
            IsReadOnly = true,
            CanSaveDraft = false,
            SaveDraftUrl = string.Empty,
            InitialConcurrencyStamp = null,
            TotalSections = definition.Sections.Count,
            TotalQuestions = definition.Sections.Sum(section => section.Questions.Count),
            Definition = definition,
            InitialResponse = sanitized,
            PriorityChoicesByValue = priorityChoices,
            ReadinessScoring = scoring,
            ScoringEvidenceLabelsByItemKey = scoringEvidenceLabels
        };

        return View(QuestionnaireViewPath, model);
    }

    private static InternalAssessmentReviewUnavailableViewModel CreateUnavailableModel(string message)
        => new(message);

    public sealed record InternalAssessmentReviewUnavailableViewModel(string Message);
}
