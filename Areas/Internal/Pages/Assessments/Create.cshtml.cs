using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentinelMfgSuite.Web.Services.Assessments;

namespace SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments;

[Authorize(Policy = "SentinelAssessmentAdmin")]
public sealed class CreateModel : PageModel
{
    private readonly IAssessmentProvisioningService _provisioningService;

    public CreateModel(IAssessmentProvisioningService provisioningService)
    {
        _provisioningService = provisioningService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        NormalizeInput();

        if (!ValidateInput())
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
                ownerUserId: User.FindFirstValue(ClaimTypes.NameIdentifier));

            result = await _provisioningService.ProvisionAsync(request, ct);
        }
        catch
        {
            ModelState.AddModelError(string.Empty, "Assessment could not be created. Please retry.");
            return Page();
        }

        if (!result.Succeeded || result.AssessmentId is null)
        {
            ModelState.AddModelError(string.Empty, "Assessment could not be created. Please verify the details and retry.");
            return Page();
        }

        TempData["AssessmentCreateStatus"] = "Draft assessment created successfully.";
        return RedirectToPage("/Assessments/Index", new { area = "Internal" });
    }

    private void NormalizeInput()
    {
        Input.AssessmentTitle = Input.AssessmentTitle?.Trim();
        Input.SiteName = Input.SiteName?.Trim();
        Input.CustomerContactEmail = Input.CustomerContactEmail?.Trim();
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
    }
}
