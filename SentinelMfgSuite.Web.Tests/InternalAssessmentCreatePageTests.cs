using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class InternalAssessmentCreatePageTests
{
    [Fact]
    public async Task OnPostAsync_With_Missing_Required_Fields_Returns_Page_And_Does_Not_Call_Provisioning()
    {
        var service = new CapturingProvisioningService();
        var model = CreatePageModel(service);
        model.Input = new CreateModel.InputModel
        {
            AssessmentTitle = "",
            SiteName = "",
            CustomerContactEmail = ""
        };

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task OnPostAsync_With_Invalid_Email_Returns_Page_And_Does_Not_Call_Provisioning()
    {
        var service = new CapturingProvisioningService();
        var model = CreatePageModel(service);
        model.Input = new CreateModel.InputModel
        {
            AssessmentTitle = "Assessment",
            SiteName = "Plant",
            CustomerContactEmail = "not-an-email"
        };

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task OnPostAsync_With_Valid_Input_Creates_Draft_And_Redirects()
    {
        var service = new CapturingProvisioningService
        {
            NextResult = AssessmentProvisioningResult.Success(Guid.NewGuid())
        };

        var model = CreatePageModel(service, userId: "sentinel-admin-1");
        model.Input = new CreateModel.InputModel
        {
            AssessmentTitle = "  Internal Draft  ",
            SiteName = "  Site A  ",
            CustomerContactEmail = "  draft@example.com  "
        };

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Assessments/Index", redirect.PageName);
        Assert.Equal("Internal", redirect.RouteValues!["area"]);

        Assert.Equal("Draft assessment created successfully.", model.TempData["AssessmentCreateStatus"]);

        Assert.Equal(1, service.CallCount);
        Assert.NotNull(service.LastRequest);
        Assert.Equal(AssessmentProvisioningInitiationSource.InternalDraftAdmin, service.LastRequest!.InitiationSource);
        Assert.Equal("Internal Draft", service.LastRequest.AssessmentTitle);
        Assert.Equal("Site A", service.LastRequest.SiteName);
        Assert.Equal("draft@example.com", service.LastRequest.CustomerContactEmail);
        Assert.Equal("sentinel-admin-1", service.LastRequest.OwnerUserId);
        Assert.Null(service.LastRequest.ExternalPurchaseReference);
    }

    [Fact]
    public async Task OnPostAsync_When_Provisioning_Fails_Returns_Page_And_Preserves_Input()
    {
        var service = new CapturingProvisioningService
        {
            NextResult = AssessmentProvisioningResult.Failure("ProvisioningFailed", "failed")
        };

        var model = CreatePageModel(service);
        model.Input = new CreateModel.InputModel
        {
            AssessmentTitle = "  Internal Draft  ",
            SiteName = "  Site A  ",
            CustomerContactEmail = "  draft@example.com  "
        };

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.ErrorCount > 0);
        Assert.Equal("Internal Draft", model.Input.AssessmentTitle);
        Assert.Equal("Site A", model.Input.SiteName);
        Assert.Equal("draft@example.com", model.Input.CustomerContactEmail);
    }

    [Fact]
    public void InputModel_Enforces_Max_Lengths_Consistent_With_Entity_Constraints()
    {
        var input = new CreateModel.InputModel
        {
            AssessmentTitle = new string('a', 201),
            SiteName = new string('b', 201),
            CustomerContactEmail = $"{new string('c', 309)}@example.com"
        };

        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(input, new ValidationContext(input), validationResults, validateAllProperties: true);

        Assert.False(isValid);
    }

    private static CreateModel CreatePageModel(CapturingProvisioningService service, string userId = "test-user")
    {
        var model = new CreateModel(service);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        }, "TestAuth"));

        model.PageContext = new PageContext
        {
            HttpContext = httpContext
        };

        model.TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider());
        return model;
    }

    private sealed class CapturingProvisioningService : IAssessmentProvisioningService
    {
        public int CallCount { get; private set; }

        public AssessmentProvisioningRequest? LastRequest { get; private set; }

        public AssessmentProvisioningResult NextResult { get; set; } = AssessmentProvisioningResult.Success(Guid.NewGuid());

        public Task<AssessmentProvisioningResult> ProvisionAsync(AssessmentProvisioningRequest request, CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(NextResult);
        }
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

        public IDictionary<string, object> LoadTempData(HttpContext context) => _values;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            _values.Clear();
            foreach (var pair in values)
            {
                _values[pair.Key] = pair.Value;
            }
        }
    }
}
