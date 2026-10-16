using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SentinelMfgSuite.Web.Controllers;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentQuestionnaireSubmitControllerTests
{
    [Fact]
    public void Submit_Has_ValidateAntiForgeryToken_Attribute()
    {
        var method = typeof(AssessmentQuestionnaireController)
            .GetMethod(nameof(AssessmentQuestionnaireController.Submit), BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public async Task Submit_Returns_Success_State_And_ConcurrencyStamp()
    {
        var questionnaireService = new StubAssessmentQuestionnaireService(
            AssessmentQuestionnaireSubmitResult.Success("next-stamp"));
        var controller = CreateController(questionnaireService);

        var request = new AssessmentQuestionnaireController.SaveDraftRequest(
            InvitationToken: "token-abc",
            QuestionnairePayload: "{\"schemaVersion\":\"1.0\",\"answers\":{}}",
            ConcurrencyStamp: "stamp-1");

        var result = await controller.Submit(Guid.NewGuid(), request);

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SubmitResponse>(json.Value);
        Assert.Equal("submitted", payload.State);
        Assert.Equal("Questionnaire submitted.", payload.Message);
        Assert.Equal("next-stamp", payload.ConcurrencyStamp);
        Assert.False(payload.CanReload);
        Assert.Empty(payload.Errors);
        Assert.Equal(1, questionnaireService.SubmitCallCount);
        Assert.Equal(request.InvitationToken, questionnaireService.LastSubmitInvitationToken);
        Assert.Equal(request.QuestionnairePayload, questionnaireService.LastSubmitPayload);
        Assert.Equal(request.ConcurrencyStamp, questionnaireService.LastSubmitConcurrencyStamp);
    }

    [Fact]
    public async Task Submit_Returns_Validation_Error_State_With_Structured_Errors()
    {
        var validationErrors = new[]
        {
            new QuestionnaireValidationError("q01", "facility_name", "Field is required.")
        };
        var questionnaireService = new StubAssessmentQuestionnaireService(
            AssessmentQuestionnaireSubmitResult.ValidationFailure(validationErrors));
        var controller = CreateController(questionnaireService);

        var result = await controller.Submit(Guid.NewGuid(), new AssessmentQuestionnaireController.SaveDraftRequest("token-abc", "{}", "stamp-2"));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SubmitResponse>(json.Value);
        Assert.Equal("validation-error", payload.State);
        Assert.Equal("Please review the highlighted questionnaire fields.", payload.Message);
        Assert.Null(payload.ConcurrencyStamp);
        Assert.False(payload.CanReload);
        Assert.Single(payload.Errors);
        Assert.Equal("q01", payload.Errors[0].QuestionId);
        Assert.Equal("facility_name", payload.Errors[0].FieldId);
        Assert.Equal("Field is required.", payload.Errors[0].Message);
    }

    [Fact]
    public async Task Submit_Returns_Conflict_State_With_Reload_Allowed()
    {
        var questionnaireService = new StubAssessmentQuestionnaireService(
            AssessmentQuestionnaireSubmitResult.ConcurrencyConflict());
        var controller = CreateController(questionnaireService);

        var result = await controller.Submit(Guid.NewGuid(), new AssessmentQuestionnaireController.SaveDraftRequest("token-abc", "{}", "stamp-3"));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SubmitResponse>(json.Value);
        Assert.Equal("conflict", payload.State);
        Assert.Equal("This questionnaire was changed in another session. Reload to review the latest version before submitting again.", payload.Message);
        Assert.Null(payload.ConcurrencyStamp);
        Assert.True(payload.CanReload);
        Assert.Empty(payload.Errors);
    }

    [Fact]
    public async Task Submit_Returns_AlreadySubmitted_State_With_Reload_Allowed()
    {
        var questionnaireService = new StubAssessmentQuestionnaireService(
            AssessmentQuestionnaireSubmitResult.AlreadySubmitted());
        var controller = CreateController(questionnaireService);

        var result = await controller.Submit(Guid.NewGuid(), new AssessmentQuestionnaireController.SaveDraftRequest("token-abc", "{}", "stamp-4"));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SubmitResponse>(json.Value);
        Assert.Equal("already-submitted", payload.State);
        Assert.Equal("This questionnaire has already been submitted. Reopen the questionnaire using the invitation link to review answers in read-only mode.", payload.Message);
        Assert.Null(payload.ConcurrencyStamp);
        Assert.True(payload.CanReload);
        Assert.Empty(payload.Errors);
    }

    [Fact]
    public async Task Submit_Returns_AccessDenied_State_With_Generic_Message()
    {
        var questionnaireService = new StubAssessmentQuestionnaireService(
            AssessmentQuestionnaireSubmitResult.AccessDenied());
        var controller = CreateController(questionnaireService);

        var result = await controller.Submit(Guid.NewGuid(), new AssessmentQuestionnaireController.SaveDraftRequest("token-abc", "{}", "stamp-5"));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SubmitResponse>(json.Value);
        Assert.Equal("access-denied", payload.State);
        Assert.Equal("This assessment invitation is not available.", payload.Message);
        Assert.Null(payload.ConcurrencyStamp);
        Assert.False(payload.CanReload);
        Assert.Empty(payload.Errors);
    }

    private static AssessmentQuestionnaireController CreateController(StubAssessmentQuestionnaireService questionnaireService)
    {
        return new AssessmentQuestionnaireController(new StubAssessmentInvitationAccessService(), questionnaireService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private sealed class StubAssessmentInvitationAccessService : IAssessmentInvitationAccessService
    {
        public Task<AssessmentInvitationAccessResult> ValidateAccessAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default)
            => Task.FromResult(AssessmentInvitationAccessResult.Denied());
    }

    private sealed class StubAssessmentQuestionnaireService : IAssessmentQuestionnaireService
    {
        private readonly AssessmentQuestionnaireSubmitResult _submitResult;

        public StubAssessmentQuestionnaireService(AssessmentQuestionnaireSubmitResult submitResult)
        {
            _submitResult = submitResult;
        }

        public int LoadCallCount { get; private set; }

        public int SaveCallCount { get; private set; }

        public int SubmitCallCount { get; private set; }

        public Guid LastSubmitInvitationId { get; private set; }

        public string? LastSubmitInvitationToken { get; private set; }

        public string? LastSubmitPayload { get; private set; }

        public string? LastSubmitConcurrencyStamp { get; private set; }

        public Task<AssessmentQuestionnaireLoadResult> LoadDraftAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default)
        {
            LoadCallCount++;
            return Task.FromResult(AssessmentQuestionnaireLoadResult.AccessDenied());
        }

        public Task<AssessmentQuestionnaireSaveResult> SaveDraftAsync(Guid invitationId, string? invitationToken, string? questionnairePayload, string? concurrencyStamp, CancellationToken ct = default)
        {
            SaveCallCount++;
            return Task.FromResult(AssessmentQuestionnaireSaveResult.AccessDenied());
        }

        public Task<AssessmentQuestionnaireSubmitResult> SubmitAsync(Guid invitationId, string? invitationToken, string? questionnairePayload, string? concurrencyStamp, CancellationToken ct = default)
        {
            SubmitCallCount++;
            LastSubmitInvitationId = invitationId;
            LastSubmitInvitationToken = invitationToken;
            LastSubmitPayload = questionnairePayload;
            LastSubmitConcurrencyStamp = concurrencyStamp;
            return Task.FromResult(_submitResult);
        }
    }
}
