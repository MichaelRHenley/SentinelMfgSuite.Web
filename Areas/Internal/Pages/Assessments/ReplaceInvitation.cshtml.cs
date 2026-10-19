using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;

namespace SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments;

[Authorize(Policy = "SentinelAssessmentAdmin")]
public sealed class ReplaceInvitationModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IAssessmentInvitationReplacementEligibilityService _eligibilityService;
    private readonly IAssessmentInvitationReplacementSendService _replacementSendService;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private string? _developmentInvitationUrl;

    public ReplaceInvitationModel(
        ApplicationDbContext db,
        IAssessmentInvitationReplacementEligibilityService eligibilityService,
        IAssessmentInvitationReplacementSendService replacementSendService,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _db = db;
        _eligibilityService = eligibilityService;
        _replacementSendService = replacementSendService;
        _environment = environment;
        _configuration = configuration;
    }

    [BindProperty(SupportsGet = true)]
    public Guid AssessmentId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid InvitationId { get; set; }

    public string AssessmentTitle { get; private set; } = string.Empty;
    public string SiteName { get; private set; } = string.Empty;
    public string ContactEmail { get; private set; } = string.Empty;
    public bool IsEligible { get; private set; }
    public string? IneligibleReason { get; private set; }
    public string? DevelopmentInvitationUrl
        => CanDisplayDevelopmentInvitationUrl(_environment.IsDevelopment(), _developmentInvitationUrl)
            ? _developmentInvitationUrl
            : null;

    public TimeSpan InvitationExpiration => AssessmentInvitationReplacementSendService.InvitationExpiration;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == AssessmentId, ct);

        if (assessment is null)
        {
            return NotFound();
        }

        PopulateFromAssessment(assessment);

        var eligibility = await _eligibilityService.EvaluateAsync(AssessmentId, InvitationId, ct);
        ApplyEligibility(eligibility);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == AssessmentId, ct);

        if (assessment is null)
        {
            return NotFound();
        }

        PopulateFromAssessment(assessment);

        // GET-time eligibility is advisory only. ReplaceAsync performs the
        // authoritative atomic eligibility check and concurrency guard.
        var result = await _replacementSendService.ReplaceAndSendAsync(
            AssessmentId,
            InvitationId,
            ct,
            (replacementInvitationId, rawToken) =>
            {
                var appBaseUrl = _configuration["AppUrls:Base"];
                if (_environment.IsDevelopment() && IsLocalBaseUrl(appBaseUrl))
                {
                    _developmentInvitationUrl = AssessmentInvitationUrlBuilder.Build(
                        appBaseUrl!,
                        replacementInvitationId,
                        rawToken);
                }
            });

        switch (result.Outcome)
        {
            case AssessmentInvitationReplacementSendOutcome.Delivered:
                TempData["AssessmentCreateStatus"] = "Invitation replaced and email delivery accepted.";
                return RedirectToPage("/Assessments/Index", new { area = "Internal" });

            case AssessmentInvitationReplacementSendOutcome.EmailSuppressed:
                if (_environment.IsDevelopment() && !string.IsNullOrWhiteSpace(DevelopmentInvitationUrl))
                {
                    Response.Headers.CacheControl = "no-store";
                }

                ModelState.AddModelError(
                    string.Empty,
                    "The replacement invitation was created, but no email was sent because outbound email is suppressed in this environment. " +
                    "The invitation has not been resent automatically.");
                IsEligible = false;
                IneligibleReason = "A replacement invitation was already issued for this assessment.";
                return Page();

            case AssessmentInvitationReplacementSendOutcome.DeliveryFailed:
                ModelState.AddModelError(
                    string.Empty,
                    "The replacement invitation was created, but email delivery failed or is ambiguous. The assessment and invitation records have been preserved. " +
                    "Because the new invitation link is not retained, a further controlled replacement is required instead of an automatic retry.");
                IsEligible = false;
                IneligibleReason = "A replacement invitation was already issued for this assessment.";
                return Page();

            case AssessmentInvitationReplacementSendOutcome.ReplacementFailed:
            default:
                ModelState.AddModelError(string.Empty, DescribeReplacementFailure(result));
                IsEligible = false;
                IneligibleReason = result.FailureMessage;
                return Page();
        }
    }

    private static bool IsLocalBaseUrl(string? appBaseUrl)
    {
        if (!Uri.TryCreate(appBaseUrl, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        return string.Equals(baseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || System.Net.IPAddress.TryParse(baseUri.Host, out var address)
                && System.Net.IPAddress.IsLoopback(address);
    }

    public static bool CanDisplayDevelopmentInvitationUrl(bool isDevelopment, string? invitationUrl)
        => isDevelopment && !string.IsNullOrWhiteSpace(invitationUrl);

    private void PopulateFromAssessment(Assessment assessment)
    {
        AssessmentTitle = assessment.Title ?? string.Empty;
        SiteName = assessment.SiteName ?? string.Empty;
        ContactEmail = assessment.ContactEmail ?? string.Empty;
    }

    private void ApplyEligibility(AssessmentInvitationReplacementEligibilityResult eligibility)
    {
        IsEligible = eligibility.IsEligible;
        IneligibleReason = eligibility.IsEligible
            ? null
            : DescribeEligibilityFailure(eligibility.FailureCode, eligibility.FailureMessage);
    }

    private static string DescribeEligibilityFailure(string? failureCode, string? failureMessage)
        => failureCode switch
        {
            "AssessmentNotFound" => "Assessment was not found.",
            "InvitationNotFound" => "Invitation was not found.",
            "InvitationAssessmentMismatch" => "Invitation is not eligible for replacement.",
            "InvitationRevoked" => "This invitation has already been revoked or replaced.",
            "IneligibleAssessmentStatus" => "Assessment is not eligible for invitation replacement.",
            "IneligibleDeliveryStatus" => "This invitation cannot be replaced because delivery is in progress, already accepted, or in an ambiguous state.",
            "InvitationAlreadyUsed" => "This invitation has already been used and cannot be replaced.",
            "AnotherActiveInvitationExists" => "Assessment already has another active invitation.",
            _ => failureMessage ?? "This invitation is not currently eligible for replacement."
        };

    private static string DescribeReplacementFailure(AssessmentInvitationReplacementSendResult result)
        => result.FailureCode switch
        {
            "InvitationRevoked" => "This invitation has already been replaced or revoked.",
            "IneligibleDeliveryStatus" => "This invitation cannot be replaced because delivery is in progress, already accepted, or in an ambiguous state.",
            "InvitationAlreadyUsed" => "This invitation has already been used and cannot be replaced.",
            "AnotherActiveInvitationExists" => "Assessment already has another active invitation.",
            "AssessmentNotFound" => "Assessment was not found.",
            "InvitationNotFound" => "Invitation was not found.",
            "IneligibleAssessmentStatus" => "Assessment is not eligible for invitation replacement.",
            _ => result.FailureMessage ?? "Invitation could not be replaced. Please verify eligibility and retry."
        };
}
