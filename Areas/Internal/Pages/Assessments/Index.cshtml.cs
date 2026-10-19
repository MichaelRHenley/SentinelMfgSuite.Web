using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments;

[Authorize(Policy = "SentinelAssessmentAdmin")]
public sealed class IndexModel : PageModel
{
    private readonly ApplicationDbContext _db;

    public IndexModel(ApplicationDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<AssessmentDashboardRow> Assessments { get; private set; } = Array.Empty<AssessmentDashboardRow>();

    public bool HasAssessments => Assessments.Count > 0;

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        var projected = await _db.Set<Assessment>()
            .AsNoTracking()
            .OrderByDescending(a => a.CreatedUtc)
            .Select(a => new
            {
                a.Id,
                a.Title,
                a.SiteName,
                a.ContactEmail,
                a.Status,
                a.CreatedUtc,
                LatestInvitation = _db.Set<AssessmentInvitation>()
                    .Where(i => i.AssessmentId == a.Id)
                    .OrderByDescending(i => i.CreatedUtc)
                    .ThenByDescending(i => i.ExpiresUtc)
                    .Select(i => new
                    {
                        i.Id,
                        i.DeliveryStatus,
                        i.ExpiresUtc,
                        i.RevokedUtc,
                        i.AccessCount,
                        i.LastAccessUtc
                    })
                    .FirstOrDefault(),
                LatestResponse = _db.Set<AssessmentResponse>()
                    .Where(r => r.AssessmentId == a.Id)
                    .OrderByDescending(r => r.LastSavedUtc)
                    .Select(r => new
                    {
                        r.IsDraft,
                        r.SubmittedUtc
                    })
                    .FirstOrDefault(),
                LatestSubmittedResponseId = _db.Set<AssessmentResponse>()
                    .Where(r => r.AssessmentId == a.Id && !r.IsDraft && r.SubmittedUtc != null)
                    .OrderByDescending(r => r.SubmittedUtc)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        Assessments = projected
            .Select(x => new AssessmentDashboardRow(
                AssessmentId: x.Id,
                Title: x.Title ?? string.Empty,
                SiteName: x.SiteName ?? string.Empty,
                ContactEmail: x.ContactEmail ?? string.Empty,
                AssessmentStatus: x.Status.ToString(),
                AssessmentStatusClass: GetAssessmentStatusClass(x.Status),
                InvitationState: GetInvitationState(x.LatestInvitation?.DeliveryStatus, x.LatestInvitation?.RevokedUtc),
                InvitationStateClass: GetInvitationStateClass(x.LatestInvitation?.DeliveryStatus, x.LatestInvitation?.RevokedUtc),
                InvitationDeliveryStatus: GetInvitationDeliveryStatus(x.LatestInvitation?.DeliveryStatus),
                InvitationDeliveryStatusClass: GetInvitationDeliveryStatusClass(x.LatestInvitation?.DeliveryStatus),
                InvitationExpiresUtc: x.LatestInvitation?.ExpiresUtc,
                QuestionnaireSubmissionStatus: FormatQuestionnaireStatus(x.LatestResponse?.IsDraft, x.LatestResponse?.SubmittedUtc),
                QuestionnaireStatusClass: GetQuestionnaireStatusClass(x.LatestResponse?.IsDraft, x.LatestResponse?.SubmittedUtc),
                CreatedUtc: x.CreatedUtc,
                IsDraftAssessment: x.Status == AssessmentStatus.Draft,
                LatestInvitationId: x.LatestInvitation?.Id,
                IsResponseSubmitted: x.LatestSubmittedResponseId.HasValue,
                IsInvitationReplaceEligible: IsInvitationReplaceEligible(
                    x.Status,
                    x.LatestInvitation?.DeliveryStatus,
                    x.LatestInvitation?.RevokedUtc,
                    x.LatestInvitation?.AccessCount,
                    x.LatestInvitation?.LastAccessUtc),
                SubmittedResponseId: x.LatestSubmittedResponseId))
            .ToList();
    }

    private static bool IsInvitationReplaceEligible(
        AssessmentStatus assessmentStatus,
        AssessmentInvitationDeliveryStatus? deliveryStatus,
        DateTime? revokedUtc,
        int? accessCount,
        DateTime? lastAccessUtc)
    {
        if (assessmentStatus != AssessmentStatus.Invited || deliveryStatus is null)
        {
            return false;
        }

        if (revokedUtc is not null)
        {
            return false;
        }

        if ((accessCount ?? 0) > 0 || lastAccessUtc is not null)
        {
            return false;
        }

        return deliveryStatus is AssessmentInvitationDeliveryStatus.Pending or AssessmentInvitationDeliveryStatus.Failed;
    }

    private static string GetInvitationState(AssessmentInvitationDeliveryStatus? deliveryStatus, DateTime? revokedUtc)
    {
        if (deliveryStatus is null)
        {
            return "None";
        }

        return revokedUtc.HasValue ? "Revoked" : "Active";
    }

    private static string GetInvitationDeliveryStatus(AssessmentInvitationDeliveryStatus? deliveryStatus)
        => deliveryStatus?.ToString() ?? "No invitation";

    private static string FormatQuestionnaireStatus(bool? isDraft, DateTime? submittedUtc)
    {
        if (isDraft is null)
        {
            return "Not Started";
        }

        if (submittedUtc.HasValue || isDraft == false)
        {
            return "Submitted";
        }

        return "In Progress";
    }

    private static string GetAssessmentStatusClass(AssessmentStatus status)
        => status switch
        {
            AssessmentStatus.Completed => "bg-success",
            AssessmentStatus.Submitted => "bg-info text-dark",
            AssessmentStatus.InProgress => "bg-warning text-dark",
            AssessmentStatus.Invited => "bg-primary",
            AssessmentStatus.Draft => "bg-secondary",
            AssessmentStatus.Cancelled => "bg-danger",
            _ => "bg-secondary"
        };

    private static string GetInvitationStateClass(AssessmentInvitationDeliveryStatus? deliveryStatus, DateTime? revokedUtc)
    {
        if (deliveryStatus is null)
        {
            return "bg-secondary";
        }

        return revokedUtc.HasValue ? "bg-danger" : "bg-success";
    }

    private static string GetInvitationDeliveryStatusClass(AssessmentInvitationDeliveryStatus? deliveryStatus)
        => deliveryStatus switch
        {
            AssessmentInvitationDeliveryStatus.Accepted => "bg-success",
            AssessmentInvitationDeliveryStatus.Failed => "bg-danger",
            AssessmentInvitationDeliveryStatus.Sending => "bg-warning text-dark",
            AssessmentInvitationDeliveryStatus.Pending => "bg-info text-dark",
            AssessmentInvitationDeliveryStatus.OutcomeUnknown => "bg-dark",
            _ => "bg-secondary"
        };

    private static string GetQuestionnaireStatusClass(bool? isDraft, DateTime? submittedUtc)
    {
        if (isDraft is null)
        {
            return "bg-secondary";
        }

        if (submittedUtc.HasValue || isDraft == false)
        {
            return "bg-success";
        }

        return "bg-warning text-dark";
    }

    public sealed record AssessmentDashboardRow(
        Guid AssessmentId,
        string Title,
        string SiteName,
        string ContactEmail,
        string AssessmentStatus,
        string AssessmentStatusClass,
        string InvitationState,
        string InvitationStateClass,
        string InvitationDeliveryStatus,
        string InvitationDeliveryStatusClass,
        DateTime? InvitationExpiresUtc,
        string QuestionnaireSubmissionStatus,
        string QuestionnaireStatusClass,
        DateTime CreatedUtc,
        bool IsDraftAssessment,
        Guid? LatestInvitationId,
        bool IsResponseSubmitted,
        bool IsInvitationReplaceEligible,
        Guid? SubmittedResponseId);
}
