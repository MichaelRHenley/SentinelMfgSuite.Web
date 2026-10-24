using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments;

namespace SentinelMfgSuite.Web.Controllers;

[AllowAnonymous]
public class AssessmentQuestionnaireController : Controller
{
    private readonly IAssessmentInvitationAccessService _accessService;
    private readonly IAssessmentQuestionnaireService _questionnaireService;

    public AssessmentQuestionnaireController(
        IAssessmentInvitationAccessService accessService,
        IAssessmentQuestionnaireService questionnaireService)
    {
        _accessService = accessService;
        _questionnaireService = questionnaireService;
    }

    [HttpGet("/assessment/questionnaire/{invitationId:guid}")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(SentinelMfgSuite.Web.Services.Security.PublicRateLimiting.InvitationAccessPolicy)]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Entry(Guid invitationId, [FromQuery(Name = "token")] string? invitationToken, CancellationToken ct = default)
    {
        ViewData["DisableThirdPartyScripts"] = true;
        ApplyInvitationResponseSecurityHeaders();

        var access = await _accessService.ValidateAccessAsync(invitationId, invitationToken, ct);
        if (!access.IsAuthorized || access.AssessmentId is null || access.InvitationId is null)
        {
            return View(CreateUnavailableModel());
        }

        var load = await _questionnaireService.LoadDraftAsync(invitationId, invitationToken, ct);
        if (load.State != AssessmentQuestionnaireLoadState.Success)
        {
            return View(CreateUnavailableModel());
        }

        return View(CreateAuthorizedModel(access, invitationId, invitationToken));
    }

    [HttpGet("/assessment/questionnaire/{invitationId:guid}/form")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(SentinelMfgSuite.Web.Services.Security.PublicRateLimiting.InvitationAccessPolicy)]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Questionnaire(Guid invitationId, [FromQuery(Name = "token")] string? invitationToken, CancellationToken ct = default)
    {
        ViewData["DisableThirdPartyScripts"] = true;
        ApplyInvitationResponseSecurityHeaders();

        var access = await _accessService.ValidateAccessAsync(invitationId, invitationToken, ct);
        if (!access.IsAuthorized || access.AssessmentId is null || access.InvitationId is null)
        {
            return View("Entry", CreateUnavailableModel());
        }

        var load = await _questionnaireService.LoadDraftAsync(invitationId, invitationToken, ct);
        if (load.State != AssessmentQuestionnaireLoadState.Success || load.Definition is null)
        {
            return View("Entry", CreateUnavailableModel());
        }

        var initialResponse = QuestionnaireResponseV1Sanitizer.SanitizeForClient(
            load.Definition,
            load.Response ?? new QuestionnaireResponseV1());

        return View(CreateQuestionnaireModel(access, invitationId, invitationToken, load.ConcurrencyStamp, load.Definition, initialResponse));
    }

    [HttpPost("/assessment/questionnaire/{invitationId:guid}/draft")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(SentinelMfgSuite.Web.Services.Security.PublicRateLimiting.QuestionnaireSavePolicy)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDraft(Guid invitationId, [FromBody] SaveDraftRequest request, CancellationToken ct = default)
    {
        ApplyInvitationResponseSecurityHeaders();

        if (request is null)
        {
            return Json(new SaveDraftResponse(
                State: "validation-error",
                Message: "Questionnaire payload is required.",
                ConcurrencyStamp: null,
                Errors: new[] { new SaveDraftValidationError(string.Empty, null, "Questionnaire payload is required.") },
                CanReload: false));
        }

        var saveResult = await _questionnaireService.SaveDraftAsync(
            invitationId,
            request.InvitationToken,
            request.QuestionnairePayload,
            request.ConcurrencyStamp,
            ct);

        return saveResult.State switch
        {
            AssessmentQuestionnaireSaveState.Success => Json(new SaveDraftResponse(
                State: "saved",
                Message: "Draft saved.",
                ConcurrencyStamp: saveResult.ConcurrencyStamp,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: false)),

            AssessmentQuestionnaireSaveState.ValidationFailure => Json(new SaveDraftResponse(
                State: "validation-error",
                Message: "Please review the highlighted questionnaire fields.",
                ConcurrencyStamp: null,
                Errors: saveResult.ValidationErrors
                    .Select(error => new SaveDraftValidationError(error.QuestionId, error.FieldId, error.Message))
                    .ToArray(),
                CanReload: false)),

            AssessmentQuestionnaireSaveState.ConcurrencyConflict => Json(new SaveDraftResponse(
                State: "conflict",
                Message: "This questionnaire was changed in another session. Reload to review the latest version before saving again.",
                ConcurrencyStamp: null,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: true)),

            AssessmentQuestionnaireSaveState.AlreadySubmitted => Json(new SaveDraftResponse(
                State: "already-submitted",
                Message: "This questionnaire has already been submitted. Reopen the questionnaire using the invitation link to review answers in read-only mode.",
                ConcurrencyStamp: null,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: true)),

            AssessmentQuestionnaireSaveState.UnsupportedQuestionnaireVersion => Json(new SaveDraftResponse(
                State: "unavailable",
                Message: "This questionnaire is not available. Please contact your Sentinel coordinator.",
                ConcurrencyStamp: null,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: false)),

            _ => Json(new SaveDraftResponse(
                State: "access-denied",
                Message: "This assessment invitation is not available.",
                ConcurrencyStamp: null,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: false)),
        };
    }

    [HttpPost("/assessment/questionnaire/{invitationId:guid}/submit")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(SentinelMfgSuite.Web.Services.Security.PublicRateLimiting.QuestionnaireSavePolicy)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid invitationId, [FromBody] SaveDraftRequest request, CancellationToken ct = default)
    {
        ApplyInvitationResponseSecurityHeaders();

        if (request is null)
        {
            return Json(new SubmitResponse(
                State: "validation-error",
                Message: "Questionnaire payload is required.",
                ConcurrencyStamp: null,
                Errors: new[] { new SaveDraftValidationError(string.Empty, null, "Questionnaire payload is required.") },
                CanReload: false));
        }

        var submitResult = await _questionnaireService.SubmitAsync(
            invitationId,
            request.InvitationToken,
            request.QuestionnairePayload,
            request.ConcurrencyStamp,
            ct);

        return submitResult.State switch
        {
            AssessmentQuestionnaireSubmitState.Success => Json(new SubmitResponse(
                State: "submitted",
                Message: "Questionnaire submitted.",
                ConcurrencyStamp: submitResult.ConcurrencyStamp,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: false)),

            AssessmentQuestionnaireSubmitState.ValidationFailure => Json(new SubmitResponse(
                State: "validation-error",
                Message: "Please review the highlighted questionnaire fields.",
                ConcurrencyStamp: null,
                Errors: submitResult.ValidationErrors
                    .Select(error => new SaveDraftValidationError(error.QuestionId, error.FieldId, error.Message))
                    .ToArray(),
                CanReload: false)),

            AssessmentQuestionnaireSubmitState.ConcurrencyConflict => Json(new SubmitResponse(
                State: "conflict",
                Message: "This questionnaire was changed in another session. Reload to review the latest version before submitting again.",
                ConcurrencyStamp: null,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: true)),

            AssessmentQuestionnaireSubmitState.AlreadySubmitted => Json(new SubmitResponse(
                State: "already-submitted",
                Message: "This questionnaire has already been submitted. Reopen the questionnaire using the invitation link to review answers in read-only mode.",
                ConcurrencyStamp: null,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: true)),

            AssessmentQuestionnaireSubmitState.UnsupportedQuestionnaireVersion => Json(new SubmitResponse(
                State: "unavailable",
                Message: "This questionnaire is not available. Please contact your Sentinel coordinator.",
                ConcurrencyStamp: null,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: false)),

            _ => Json(new SubmitResponse(
                State: "access-denied",
                Message: "This assessment invitation is not available.",
                ConcurrencyStamp: null,
                Errors: Array.Empty<SaveDraftValidationError>(),
                CanReload: false)),
        };
    }

    private void ApplyInvitationResponseSecurityHeaders()
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
    }

    private AssessmentQuestionnaireWelcomeViewModel CreateUnavailableModel()
        => new()
        {
            IsInvitationAvailable = false,
            StatusLabel = "Unavailable",
            StatusDescription = "This invitation is not available.",
            IsReadOnly = false,
            PrimaryActionLabel = "Assessment unavailable",
            PrimaryActionEnabled = false,
            PrimaryActionUrl = null,
            PrimaryActionNote = "Please contact your Sentinel coordinator if you need a new invitation."
        };

    private AssessmentQuestionnaireWelcomeViewModel CreateAuthorizedModel(AssessmentInvitationAccessResult access, Guid invitationId, string? invitationToken)
    {
        var statusLabel = access.ResponseState switch
        {
            AssessmentInvitationResponseState.NotStarted => "Not Started",
            AssessmentInvitationResponseState.Draft => "In Progress",
            AssessmentInvitationResponseState.Submitted => "Submitted",
            _ => "Not Started"
        };

        var statusDescription = access.ResponseState switch
        {
            AssessmentInvitationResponseState.NotStarted => "You have not started this assessment yet.",
            AssessmentInvitationResponseState.Draft => "Your response is in progress. You can continue from your saved draft while this invitation remains valid.",
            AssessmentInvitationResponseState.Submitted => "Your responses have been submitted and are now locked for read-only review.",
            _ => "You can continue this assessment while your invitation remains valid."
        };

        var actionLabel = access.ResponseState switch
        {
            AssessmentInvitationResponseState.NotStarted => "Start Assessment",
            AssessmentInvitationResponseState.Draft => "Resume Assessment",
            AssessmentInvitationResponseState.Submitted => "Review Answers",
            _ => "Continue"
        };

        return new AssessmentQuestionnaireWelcomeViewModel
        {
            IsInvitationAvailable = true,
            StatusLabel = statusLabel,
            StatusDescription = statusDescription,
            IsReadOnly = access.IsReadOnly || access.ResponseState == AssessmentInvitationResponseState.Submitted,
            PrimaryActionLabel = actionLabel,
            PrimaryActionEnabled = true,
            PrimaryActionUrl = BuildQuestionnaireUrl(invitationId, invitationToken),
            PrimaryActionNote = ""
        };
    }

    private static string BuildQuestionnaireUrl(Guid invitationId, string? invitationToken)
    {
        var basePath = $"/assessment/questionnaire/{invitationId:D}/form";
        if (string.IsNullOrWhiteSpace(invitationToken))
        {
            return basePath;
        }

        return $"{basePath}?token={Uri.EscapeDataString(invitationToken)}";
    }

    private static AssessmentQuestionnairePageViewModel CreateQuestionnaireModel(
        AssessmentInvitationAccessResult access,
        Guid invitationId,
        string? invitationToken,
        string? concurrencyStamp,
        QuestionnaireDefinition definition,
        QuestionnaireResponseV1 initialResponse)
    {
        var statusLabel = access.ResponseState switch
        {
            AssessmentInvitationResponseState.NotStarted => "Not Started",
            AssessmentInvitationResponseState.Draft => "In Progress",
            AssessmentInvitationResponseState.Submitted => "Submitted",
            _ => "Not Started"
        };

        var q06Choices = definition.Sections
            .SelectMany(section => section.Questions)
            .Single(question => string.Equals(question.Id, "q06", StringComparison.Ordinal))
            .Choices ?? new List<Choice>();

        var priorityChoices = q06Choices
            .ToDictionary(choice => choice.Value, choice => choice.Label, StringComparer.Ordinal);

        var isReadOnly = access.IsReadOnly || access.ResponseState == AssessmentInvitationResponseState.Submitted;

        return new AssessmentQuestionnairePageViewModel
        {
            InvitationId = invitationId,
            InvitationToken = invitationToken ?? string.Empty,
            StatusLabel = statusLabel,
            IsReadOnly = isReadOnly,
            CanSaveDraft = !isReadOnly,
            SaveDraftUrl = $"/assessment/questionnaire/{invitationId:D}/draft",
            InitialConcurrencyStamp = concurrencyStamp,
            TotalSections = definition.Sections.Count,
            TotalQuestions = definition.Sections.Sum(section => section.Questions.Count),
            Definition = definition,
            InitialResponse = initialResponse,
            PriorityChoicesByValue = priorityChoices
        };
    }

    public sealed record SaveDraftRequest(string? InvitationToken, string? QuestionnairePayload, string? ConcurrencyStamp);

    public sealed record SaveDraftValidationError(string QuestionId, string? FieldId, string Message);

    public sealed record SaveDraftResponse(string State, string Message, string? ConcurrencyStamp, IReadOnlyList<SaveDraftValidationError> Errors, bool CanReload);

    public sealed record SubmitResponse(string State, string Message, string? ConcurrencyStamp, IReadOnlyList<SaveDraftValidationError> Errors, bool CanReload);
}
