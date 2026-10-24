using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Services.Assessments;

namespace SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments;

[Authorize(Policy = "SentinelAssessmentAdmin")]
public sealed class CreateModel : PageModel
{
    private readonly IAssessmentProvisioningService _provisioningService;
    private readonly SentinelMfgSuite.Web.Data.ApplicationDbContext? _db;

    public CreateModel(IAssessmentProvisioningService provisioningService)
    {
        _provisioningService = provisioningService;
    }

    private readonly Microsoft.Extensions.Options.IOptions<SentinelMfgSuite.Web.Services.Assessments.Payments.AssessmentPricingOptions>? _pricing;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public CreateModel(IAssessmentProvisioningService provisioningService, SentinelMfgSuite.Web.Data.ApplicationDbContext db,
        Microsoft.Extensions.Options.IOptions<SentinelMfgSuite.Web.Services.Assessments.Payments.AssessmentPricingOptions> pricing)
    {
        _provisioningService = provisioningService;
        _db = db;
        _pricing = pricing;
    }

    public bool PaymentRequired => _pricing?.Value.EnforcePayment == true;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        NormalizeInput();

        var inputValid = ValidateInput();
        if (PaymentRequired)
        {
            foreach (var (field, message) in SentinelMfgSuite.Web.Services.Assessments.Payments.AssessmentBillingValidator.Validate(
                Input.CustomerOrganization, Input.BillingContactName, Input.BillingContactEmail))
            {
                if (!ModelState.TryGetValue($"Input.{field}", out var entry) || entry.Errors.Count == 0)
                {
                    ModelState.AddModelError($"Input.{field}", message);
                }
                inputValid = false;
            }
        }

        if (!inputValid)
        {
            return Page();
        }

        AssessmentProvisioningResult result;
        try
        {
            var request = new AssessmentProvisioningRequest(
                AssessmentProvisioningInitiationSource.InternalDraftAdmin,
                Input.AssessmentTitle!,
                Input.SiteName!,
                Input.CustomerContactEmail!,
                ownerUserId: User.FindFirstValue(ClaimTypes.NameIdentifier))
            {
                CustomerOrganization = Input.CustomerOrganization,
                BillingContactName = Input.BillingContactName,
                BillingContactEmail = Input.BillingContactEmail
            };

            result = await _provisioningService.ProvisionAsync(request, ct);
        }
        catch
        {
            ModelState.AddModelError(string.Empty, "Assessment could not be created. Please retry.");
            return Page();
        }

        if (!result.Succeeded || result.AssessmentId is null)
        {
            ModelState.AddModelError(string.Empty, result.FailureCode is "BillingInformationInvalid" or "PricingNotConfigured"
                ? result.FailureMessage ?? "Assessment could not be created."
                : "Assessment could not be created. Please verify the details and retry.");
            return Page();
        }

        TempData["AssessmentCreateStatus"] = "Draft assessment created successfully.";
        var hasOrder = _db is not null && await _db.Set<SentinelMfgSuite.Web.Models.Assessments.AssessmentOrder>().AnyAsync(o => o.AssessmentId == result.AssessmentId, ct);
        if (hasOrder)
        {
            TempData["AssessmentCreateStatus"] = "Assessment created with a Pending order. The invitation is issued only after payment is verified.";
            return RedirectToPage("/Assessments/Order", new { area = "Internal", assessmentId = result.AssessmentId });
        }
        return RedirectToPage("/Assessments/Index", new { area = "Internal" });
    }

    private void NormalizeInput()
    {
        Input.AssessmentTitle = Input.AssessmentTitle?.Trim();
        Input.SiteName = Input.SiteName?.Trim();
        Input.CustomerContactEmail = Input.CustomerContactEmail?.Trim();
        Input.CustomerOrganization = Input.CustomerOrganization?.Trim();
        Input.BillingContactName = Input.BillingContactName?.Trim();
        Input.BillingContactEmail = Input.BillingContactEmail?.Trim();
    }

    private bool ValidateInput()
    {
        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(Input, new ValidationContext(Input), validationResults, validateAllProperties: true);

        if (isValid)
        {
            return true;
        }

        foreach (var validationResult in validationResults)
        {
            var members = validationResult.MemberNames?.Any() == true
                ? validationResult.MemberNames
                : new[] { string.Empty };

            foreach (var member in members)
            {
                var key = string.IsNullOrWhiteSpace(member) ? string.Empty : $"Input.{member}";
                ModelState.AddModelError(key, validationResult.ErrorMessage ?? "Invalid input.");
            }
        }

        return false;
    }

    public sealed class InputModel
    {
        [Required]
        [StringLength(200)]
        [Display(Name = "Assessment title")]
        public string? AssessmentTitle { get; set; }

        [Required]
        [StringLength(200)]
        [Display(Name = "Manufacturing site name")]
        public string? SiteName { get; set; }

        [Required]
        [EmailAddress]
        [StringLength(320)]
        [Display(Name = "Customer contact email")]
        public string? CustomerContactEmail { get; set; }

        [StringLength(200)]
        [Display(Name = "Customer organization")]
        public string? CustomerOrganization { get; set; }

        [StringLength(200)]
        [Display(Name = "Billing contact name")]
        public string? BillingContactName { get; set; }

        [EmailAddress]
        [StringLength(320)]
        [Display(Name = "Billing contact email")]
        public string? BillingContactEmail { get; set; }
    }
}
