using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SentinelMfgSuite.Web.Controllers;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentQuestionnaireControllerTests
{
    [Fact]
    public void SaveDraft_Has_ValidateAntiForgeryToken_Attribute()
    {
        var method = typeof(AssessmentQuestionnaireController)
            .GetMethod(nameof(AssessmentQuestionnaireController.SaveDraft), BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public async Task Entry_Returns_Generic_Unavailable_Page_For_Denied_Access()
    {
        var accessService = new StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult.Denied());
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.Success(new QuestionnaireResponseV1(), null, false),
            saveResult: AssessmentQuestionnaireSaveResult.Success("stamp-1"));
        var controller = CreateController(accessService, questionnaireService);

        var result = await controller.Entry(Guid.NewGuid(), "opaque-token");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AssessmentQuestionnaireWelcomeViewModel>(view.Model);
        Assert.False(model.IsInvitationAvailable);
        Assert.Equal("Unavailable", model.StatusLabel);
        Assert.Equal(0, questionnaireService.LoadCallCount);
        AssertInvitationHeaders(controller);
    }

    [Fact]
    public async Task Entry_Returns_Enabled_Start_Link_For_Authorized_NotStarted_Invitation()
    {
        var invitationId = Guid.NewGuid();
        var accessService = new StubAssessmentInvitationAccessService(
            AssessmentInvitationAccessResult.Authorized(Guid.NewGuid(), invitationId, null, AssessmentInvitationResponseState.NotStarted, false));
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.Success(new QuestionnaireResponseV1(), null, false),
            saveResult: AssessmentQuestionnaireSaveResult.Success("stamp-1"));
        var controller = CreateController(accessService, questionnaireService);

        var result = await controller.Entry(invitationId, "opaque-token");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AssessmentQuestionnaireWelcomeViewModel>(view.Model);
        Assert.True(model.IsInvitationAvailable);
        Assert.Equal("Not Started", model.StatusLabel);
        Assert.Equal("Start Assessment", model.PrimaryActionLabel);
        Assert.True(model.PrimaryActionEnabled);
        Assert.NotNull(model.PrimaryActionUrl);
        Assert.Contains("/assessment/questionnaire/", model.PrimaryActionUrl, StringComparison.Ordinal);
        Assert.Contains("/form", model.PrimaryActionUrl, StringComparison.Ordinal);
        Assert.Contains("token=opaque-token", model.PrimaryActionUrl, StringComparison.Ordinal);
        Assert.Equal(1, questionnaireService.LoadCallCount);
        AssertInvitationHeaders(controller);
    }

    [Fact]
    public async Task Questionnaire_Returns_Empty_State_For_Authorized_New_Invitation()
    {
        var invitationId = Guid.NewGuid();
        var accessService = new StubAssessmentInvitationAccessService(
            AssessmentInvitationAccessResult.Authorized(Guid.NewGuid(), invitationId, null, AssessmentInvitationResponseState.NotStarted, false));
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.Success(new QuestionnaireResponseV1(), "stamp-1", false),
            saveResult: AssessmentQuestionnaireSaveResult.Success("stamp-2"));
        var controller = CreateController(accessService, questionnaireService);

        var result = await controller.Questionnaire(invitationId, "opaque-token");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AssessmentQuestionnairePageViewModel>(view.Model);
        Assert.Equal(invitationId, model.InvitationId);
        Assert.Equal("opaque-token", model.InvitationToken);
        Assert.Equal("Not Started", model.StatusLabel);
        Assert.False(model.IsReadOnly);
        Assert.True(model.CanSaveDraft);
        Assert.Equal($"/assessment/questionnaire/{invitationId:D}/draft", model.SaveDraftUrl);
        Assert.Equal("stamp-1", model.InitialConcurrencyStamp);
        Assert.Empty(model.InitialResponse.Answers);
        Assert.Equal(1, questionnaireService.LoadCallCount);
        AssertInvitationHeaders(controller);
    }

    [Fact]
    public async Task Questionnaire_Returns_Submitted_ReadOnly_State_For_Authorized_Submitted_Response()
    {
        var invitationId = Guid.NewGuid();
        var response = new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q04"] = new QuestionAnswer { Selected = "unknown" }
            }
        };

        var accessService = new StubAssessmentInvitationAccessService(
            AssessmentInvitationAccessResult.Authorized(Guid.NewGuid(), invitationId, Guid.NewGuid(), AssessmentInvitationResponseState.Submitted, true));
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.Success(response, "stamp-1", true),
            saveResult: AssessmentQuestionnaireSaveResult.AlreadySubmitted());
        var controller = CreateController(accessService, questionnaireService);

        var result = await controller.Questionnaire(invitationId, "opaque-token");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AssessmentQuestionnairePageViewModel>(view.Model);
        Assert.Equal("Submitted", model.StatusLabel);
        Assert.True(model.IsReadOnly);
        Assert.False(model.CanSaveDraft);
        Assert.Equal("unknown", model.InitialResponse.Answers["q04"].Selected);
        Assert.Equal(1, questionnaireService.LoadCallCount);
        AssertInvitationHeaders(controller);
    }

    [Fact]
    public async Task SaveDraft_Returns_Saved_State_And_New_ConcurrencyStamp()
    {
        var invitationId = Guid.NewGuid();
        var accessService = new StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult.Denied());
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.AccessDenied(),
            saveResult: AssessmentQuestionnaireSaveResult.Success("new-stamp"));
        var controller = CreateController(accessService, questionnaireService);

        var request = new AssessmentQuestionnaireController.SaveDraftRequest("opaque-token", "{\"schemaVersion\":\"1.0\",\"answers\":{}}", null);
        var result = await controller.SaveDraft(invitationId, request);

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SaveDraftResponse>(json.Value);
        Assert.Equal("saved", payload.State);
        Assert.Equal("new-stamp", payload.ConcurrencyStamp);
        Assert.Equal(1, questionnaireService.SaveCallCount);
        Assert.Equal(invitationId, questionnaireService.LastSaveInvitationId);
        Assert.Equal("opaque-token", questionnaireService.LastSaveInvitationToken);
        Assert.Equal(request.QuestionnairePayload, questionnaireService.LastSavePayload);
        Assert.Null(questionnaireService.LastSaveConcurrencyStamp);
        AssertInvitationHeaders(controller);
    }

    [Fact]
    public async Task SaveDraft_Returns_Validation_Error_State_With_Structured_Errors()
    {
        var invitationId = Guid.NewGuid();
        var accessService = new StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult.Denied());
        var validationErrors = new[] { new QuestionnaireValidationError("q01", "facility_name", "Field is required.") };
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.AccessDenied(),
            saveResult: AssessmentQuestionnaireSaveResult.ValidationFailure(validationErrors));
        var controller = CreateController(accessService, questionnaireService);

        var request = new AssessmentQuestionnaireController.SaveDraftRequest("opaque-token", "{}", "stamp-1");
        var result = await controller.SaveDraft(invitationId, request);

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SaveDraftResponse>(json.Value);
        Assert.Equal("validation-error", payload.State);
        Assert.Single(payload.Errors);
        Assert.Equal("q01", payload.Errors[0].QuestionId);
        Assert.Equal("facility_name", payload.Errors[0].FieldId);
        Assert.Equal("stamp-1", questionnaireService.LastSaveConcurrencyStamp);
        AssertInvitationHeaders(controller);
    }

    [Fact]
    public async Task SaveDraft_Returns_Conflict_State_Without_AutoReload()
    {
        var accessService = new StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult.Denied());
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.AccessDenied(),
            saveResult: AssessmentQuestionnaireSaveResult.ConcurrencyConflict());
        var controller = CreateController(accessService, questionnaireService);

        var result = await controller.SaveDraft(Guid.NewGuid(), new AssessmentQuestionnaireController.SaveDraftRequest("opaque-token", "{}", "stamp-1"));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SaveDraftResponse>(json.Value);
        Assert.Equal("conflict", payload.State);
        Assert.True(payload.CanReload);
        AssertInvitationHeaders(controller);
    }

    [Fact]
    public async Task SaveDraft_Returns_AlreadySubmitted_State()
    {
        var accessService = new StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult.Denied());
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.AccessDenied(),
            saveResult: AssessmentQuestionnaireSaveResult.AlreadySubmitted());
        var controller = CreateController(accessService, questionnaireService);

        var result = await controller.SaveDraft(Guid.NewGuid(), new AssessmentQuestionnaireController.SaveDraftRequest("opaque-token", "{}", "stamp-1"));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SaveDraftResponse>(json.Value);
        Assert.Equal("already-submitted", payload.State);
        Assert.True(payload.CanReload);
        AssertInvitationHeaders(controller);
    }

    [Fact]
    public async Task SaveDraft_Returns_AccessDenied_State_With_Generic_Message()
    {
        var accessService = new StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult.Denied());
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.AccessDenied(),
            saveResult: AssessmentQuestionnaireSaveResult.AccessDenied());
        var controller = CreateController(accessService, questionnaireService);

        var result = await controller.SaveDraft(Guid.NewGuid(), new AssessmentQuestionnaireController.SaveDraftRequest("opaque-token", "{}", "stamp-1"));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SaveDraftResponse>(json.Value);
        Assert.Equal("access-denied", payload.State);
        Assert.Equal("This assessment invitation is not available.", payload.Message);
        AssertInvitationHeaders(controller);
    }

    [Fact]
    public async Task SaveDraft_Returns_Validation_Error_When_Client_Submits_Empty_Payload()
    {
        var accessService = new StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult.Denied());
        var questionnaireService = new StubAssessmentQuestionnaireService(
            loadResult: AssessmentQuestionnaireLoadResult.AccessDenied(),
            saveResult: AssessmentQuestionnaireSaveResult.AccessDenied());
        var controller = CreateController(accessService, questionnaireService);

        var result = await controller.SaveDraft(Guid.NewGuid(), new AssessmentQuestionnaireController.SaveDraftRequest("opaque-token", "{}", "stamp-1"));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<AssessmentQuestionnaireController.SaveDraftResponse>(json.Value);
        Assert.Equal("access-denied", payload.State);
        Assert.Equal("This assessment invitation is not available.", payload.Message);
        AssertInvitationHeaders(controller);
    }

    private static AssessmentQuestionnaireController CreateController(
        IAssessmentInvitationAccessService accessService,
        StubAssessmentQuestionnaireService questionnaireService)
    {
        return new AssessmentQuestionnaireController(accessService, questionnaireService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static void AssertInvitationHeaders(Controller controller)
    {
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("no-store, no-cache, must-revalidate, max-age=0", controller.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("no-cache", controller.Response.Headers["Pragma"].ToString());
        Assert.Equal("0", controller.Response.Headers["Expires"].ToString());
    }

    private sealed class StubAssessmentInvitationAccessService : IAssessmentInvitationAccessService
    {
        private readonly AssessmentInvitationAccessResult _result;

        public StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult result)
        {
            _result = result;
        }

        public Task<AssessmentInvitationAccessResult> ValidateAccessAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    private sealed class StubAssessmentQuestionnaireService : IAssessmentQuestionnaireService
    {
        private readonly AssessmentQuestionnaireLoadResult _loadResult;
        private readonly AssessmentQuestionnaireSaveResult _saveResult;

        public StubAssessmentQuestionnaireService(AssessmentQuestionnaireLoadResult loadResult, AssessmentQuestionnaireSaveResult saveResult)
        {
            _loadResult = loadResult;
            _saveResult = saveResult;
        }

        public int LoadCallCount { get; private set; }

        public int SaveCallCount { get; private set; }

        public Guid LastSaveInvitationId { get; private set; }

        public string? LastSaveInvitationToken { get; private set; }

        public string? LastSavePayload { get; private set; }

        public string? LastSaveConcurrencyStamp { get; private set; }

        public Task<AssessmentQuestionnaireLoadResult> LoadDraftAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default)
        {
            LoadCallCount++;
            return Task.FromResult(_loadResult);
        }

        public Task<AssessmentQuestionnaireSaveResult> SaveDraftAsync(Guid invitationId, string? invitationToken, string? questionnairePayload, string? concurrencyStamp, CancellationToken ct = default)
        {
            SaveCallCount++;
            LastSaveInvitationId = invitationId;
            LastSaveInvitationToken = invitationToken;
            LastSavePayload = questionnairePayload;
            LastSaveConcurrencyStamp = concurrencyStamp;
            return Task.FromResult(_saveResult);
        }

        public Task<AssessmentQuestionnaireSubmitResult> SubmitAsync(Guid invitationId, string? invitationToken, string? questionnairePayload, string? concurrencyStamp, CancellationToken ct = default)
            => Task.FromResult(AssessmentQuestionnaireSubmitResult.AccessDenied());
    }
}
