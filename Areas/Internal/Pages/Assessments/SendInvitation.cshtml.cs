using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;

namespace SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments;

[Authorize(Policy = "SentinelAssessmentAdmin")]
public sealed class SendInvitationModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IAssessmentInvitationSendService _sendService;

    public SendInvitationModel(ApplicationDbContext db, IAssessmentInvitationSendService sendService)
    {
        _db = db;
        _sendService = sendService;
    }

    [BindProperty(SupportsGet = true)]
    public Guid AssessmentId { get; set; }

    public string AssessmentTitle { get; private set; } = string.Empty;
    public string SiteName { get; private set; } = string.Empty;
    public string ContactEmail { get; private set; } = string.Empty;
    public bool IsEligible { get; private set; }
    public string? IneligibleReason { get; private set; }

    public TimeSpan InvitationExpiration => AssessmentInvitationSendService.InvitationExpiration;

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

        var result = await _sendService.SendInvitationAsync(AssessmentId, ct);

        switch (result.Outcome)
        {
            case AssessmentInvitationSendOutcome.Delivered:
                TempData["AssessmentCreateStatus"] = "Invitation issued and email delivery accepted.";
                return RedirectToPage("/Assessments/Index", new { area = "Internal" });

            case AssessmentInvitationSendOutcome.EmailSuppressed:
                ModelState.AddModelError(
                    string.Empty,
                    "The invitation was created, but no email was sent because outbound email is suppressed in this environment. " +
                    "The invitation has not been resent automatically.");
                IsEligible = false;
                IneligibleReason = "An invitation was already issued for this assessment.";
                return Page();

            case AssessmentInvitationSendOutcome.DeliveryFailed:
                ModelState.AddModelError(
                    string.Empty,
                    "The invitation was created, but email delivery failed. The assessment and invitation records have been preserved. " +
                    "Because the original invitation link is not retained, a controlled replacement is required instead of an automatic retry.");
                IsEligible = false;
                IneligibleReason = "An invitation was already issued for this assessment.";
                return Page();

            case AssessmentInvitationSendOutcome.IssuanceFailed:
            default:
                ModelState.AddModelError(string.Empty, DescribeIssuanceFailure(result));
                IsEligible = false;
                IneligibleReason = result.FailureMessage;
                return Page();
        }
    }

    private void PopulateFromAssessment(Assessment assessment)
    {
        AssessmentTitle = assessment.Title ?? string.Empty;
        SiteName = assessment.SiteName ?? string.Empty;
        ContactEmail = assessment.ContactEmail ?? string.Empty;
        IsEligible = assessment.Status == AssessmentStatus.Draft;
        IneligibleReason = IsEligible
            ? null
            : "Only assessments in Draft status are eligible to be invited.";
    }

    private static string DescribeIssuanceFailure(AssessmentInvitationSendResult result)
        => result.FailureCode switch
        {
            "DuplicateActiveInvitation" => "An active invitation already exists for this assessment.",
            "IneligibleAssessmentStatus" => "Only draft assessments can be invited.",
            "AssessmentNotFound" => "Assessment was not found.",
            "MissingContactEmail" => "Assessment contact email is required for invitation issuance.",
            _ => result.FailureMessage ?? "Invitation could not be issued. Please verify the assessment and retry."
        };
}
