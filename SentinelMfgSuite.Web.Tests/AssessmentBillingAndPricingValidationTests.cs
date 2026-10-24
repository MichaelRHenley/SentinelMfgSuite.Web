using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Payments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentBillingAndPricingValidationTests
{
    [Theory]
    [InlineData(null, "Pat", "a@example.com", "CustomerOrganization")]
    [InlineData("  ", "Pat", "a@example.com", "CustomerOrganization")]
    [InlineData("Acme", "", "a@example.com", "BillingContactName")]
    [InlineData("Acme", "Pat", null, "BillingContactEmail")]
    [InlineData("Acme", "Pat", "not-an-email", "BillingContactEmail")]
    public void Billing_Validator_Rejects_Missing_Or_Invalid(string? org, string? name, string? email, string field)
    {
        Assert.Contains(AssessmentBillingValidator.Validate(org, name, email), e => e.Field == field);
    }

    [Fact]
    public void Billing_Validator_Accepts_Valid() =>
        Assert.Empty(AssessmentBillingValidator.Validate("Acme Mfg Ltd.", "Pat Buyer", "ap@example.com"));

    private static ValidateOptionsResult Run(Dictionary<string, string?> cfg)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(cfg).Build();
        var o = new AssessmentPricingOptions();
        config.GetSection(AssessmentPricingOptions.SectionName).Bind(o);
        return new AssessmentPricingOptionsValidator().Validate(null, o);
    }

    private static Dictionary<string, string?> Good() => new()
    {
        ["AssessmentPricing:BasePrice"] = "2500.00",
        ["AssessmentPricing:Currency"] = "CAD",
        ["AssessmentPricing:TaxTreatment"] = "None",
        ["AssessmentPricing:TaxRate"] = "0",
        ["AssessmentPricing:EnforcePayment"] = "true"
    };

    [Fact]
    public void Configured_Initial_Values_Are_Valid() => Assert.True(Run(Good()).Succeeded);

    [Fact]
    public void Missing_Section_Fails_When_Enforced() => Assert.True(Run(new()).Failed);

    [Theory]
    [InlineData("AssessmentPricing:BasePrice", "0")]
    [InlineData("AssessmentPricing:BasePrice", "-5")]
    [InlineData("AssessmentPricing:BasePrice", "10.005")]
    [InlineData("AssessmentPricing:Currency", "USD")]
    [InlineData("AssessmentPricing:TaxRate", "0.05")]
    [InlineData("AssessmentPricing:TaxTreatment", "Exclusive")]
    public void Invalid_Values_Fail(string key, string value)
    {
        var cfg = Good();
        cfg[key] = value;
        Assert.True(Run(cfg).Failed);
    }

    [Fact]
    public void Disabled_Enforcement_Skips_Validation() =>
        Assert.True(Run(new() { ["AssessmentPricing:EnforcePayment"] = "false" }).Succeeded);

    [Fact]
    public void ValidateOnStart_Throws_Clear_Error_For_Invalid_Config()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AssessmentPricing:BasePrice"] = "0", ["AssessmentPricing:EnforcePayment"] = "true"
        }).Build();
        var services = new ServiceCollection();
        services.AddOptions<AssessmentPricingOptions>().Bind(config.GetSection(AssessmentPricingOptions.SectionName));
        services.AddSingleton<IValidateOptions<AssessmentPricingOptions>, AssessmentPricingOptionsValidator>();
        using var sp = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<AssessmentPricingOptions>>().Value);
        Assert.Contains("BasePrice", ex.Message);
    }

    [Fact]
    public void Shipped_Appsettings_Contain_Initial_Pricing()
    {
        foreach (var f in new[] { "appsettings.json", "appsettings.Development.json", "appsettings.Production.json" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, f);
            if (!File.Exists(path)) path = Path.Combine(FindRoot(), f);
            var cfg = new ConfigurationBuilder().AddJsonFile(path).Build();
            var o = new AssessmentPricingOptions();
            cfg.GetSection(AssessmentPricingOptions.SectionName).Bind(o);
            Assert.Equal(2500.00m, o.BasePrice);
            Assert.Equal("CAD", o.Currency);
            Assert.Equal(AssessmentTaxTreatment.None, o.TaxTreatment);
            Assert.Equal(0m, o.TaxRate);
            Assert.True(o.EnforcePayment);
            Assert.Null(o.Validate());
        }
    }

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "appsettings.json"))) d = d.Parent;
        return d!.FullName;
    }

    private sealed class CountingService : IAssessmentProvisioningService
    {
        public int Calls;
        public Task<AssessmentProvisioningResult> ProvisionAsync(AssessmentProvisioningRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(AssessmentProvisioningResult.Success(Guid.NewGuid()));
        }
    }

    private static async Task<(CreateModel model, CountingService svc, IActionResult result)> Post(CreateModel.InputModel input)
    {
        var svc = new CountingService();
        var model = new CreateModel(svc, null!, Options.Create(new AssessmentPricingOptions { BasePrice = 2500m, EnforcePayment = true }))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
            Input = input
        };
        model.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(model.HttpContext, new NullTempDataProvider());
        return (model, svc, await model.OnPostAsync());
    }

    private sealed class NullTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public async Task Create_Page_Shows_Billing_Messages_And_Does_Not_Provision_When_Payment_Required()
    {
        var (model, svc, result) = await Post(new CreateModel.InputModel { AssessmentTitle = "T", SiteName = "S", CustomerContactEmail = "c@example.com" });
        Assert.IsType<PageResult>(result);
        Assert.Equal(0, svc.Calls);
        Assert.NotEmpty(model.ModelState["Input.CustomerOrganization"]!.Errors);
        Assert.NotEmpty(model.ModelState["Input.BillingContactName"]!.Errors);
        Assert.NotEmpty(model.ModelState["Input.BillingContactEmail"]!.Errors);
    }

    [Fact]
    public async Task Create_Page_Rejects_Invalid_Billing_Email()
    {
        var (model, svc, result) = await Post(new CreateModel.InputModel
        { AssessmentTitle = "T", SiteName = "S", CustomerContactEmail = "c@example.com", CustomerOrganization = "Acme", BillingContactName = "Pat", BillingContactEmail = "bad" });
        Assert.IsType<PageResult>(result);
        Assert.Equal(0, svc.Calls);
        Assert.NotEmpty(model.ModelState["Input.BillingContactEmail"]!.Errors);
    }
}