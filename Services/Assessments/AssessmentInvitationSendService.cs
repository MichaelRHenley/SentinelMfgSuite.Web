using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments;

/// <summary>
/// Orchestrates sending an invitation for an existing Draft assessment.
/// Issues the invitation (which commits its own transaction) and only
/// attempts delivery after that commit succeeds. Never creates another
/// assessment and never logs or persists the raw invitation token.
/// </summary>
public sealed class AssessmentInvitationSendService : IAssessmentInvitationSendService
{
    public static readonly TimeSpan InvitationExpiration = TimeSpan.FromDays(7);

    private readonly ApplicationDbContext _db;
    private readonly IAssessmentInvitationIssuanceService _issuanceService;
    private readonly IAssessmentInvitationDeliveryService _deliveryService;

    public AssessmentInvitationSendService(
        ApplicationDbContext db,
        IAssessmentInvitationIssuanceService issuanceService,
        IAssessmentInvitationDeliveryService deliveryService)
    {
        _db = db;
        _issuanceService = issuanceService;
        _deliveryService = deliveryService;
    }

    public async Task<AssessmentInvitationSendResult> SendInvitationAsync(Guid assessmentId, CancellationToken ct = default)
    {
        if (assessmentId == Guid.Empty)
        {
            return AssessmentInvitationSendResult.IssuanceFailed("InvalidAssessmentId", "Assessment id is required.");
        }

        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == assessmentId, ct);

        if (assessment is null)
        {
            return AssessmentInvitationSendResult.IssuanceFailed("AssessmentNotFound", "Assessment was not found.");
        }

        if (assessment.Status != AssessmentStatus.Draft)
        {
            return AssessmentInvitationSendResult.IssuanceFailed("IneligibleAssessmentStatus", "Only draft assessments can be invited.");
        }

        if (string.IsNullOrWhiteSpace(assessment.ContactEmail))
        {
            return AssessmentInvitationSendResult.IssuanceFailed("MissingContactEmail", "Assessment contact email is required for invitation issuance.");
        }

        // Issuance atomically transitions Draft -> Invited and commits its own transaction.
        var issuance = await _issuanceService.IssueAsync(assessmentId, InvitationExpiration, ct);

        if (!issuance.Succeeded
            || issuance.InvitationId is null
            || issuance.ExpiresUtc is null
            || string.IsNullOrWhiteSpace(issuance.InvitationToken))
        {
            return AssessmentInvitationSendResult.IssuanceFailed(
                issuance.FailureCode ?? "IssuanceFailed",
                issuance.FailureMessage ?? "Invitation could not be issued.");
        }

        var invitationId = issuance.InvitationId.Value;
        var rawInvitationToken = issuance.InvitationToken;
        var expiresUtc = issuance.ExpiresUtc.Value;

        // Delivery is only attempted after issuance has committed.
        var deliveryRequest = new AssessmentInvitationDeliveryRequest(
            assessmentId,
            invitationId,
            assessment.ContactEmail,
            rawInvitationToken,
            expiresUtc);

        var deliveryResult = await _deliveryService.DeliverAsync(deliveryRequest, ct);

        if (deliveryResult.Succeeded)
        {
            return AssessmentInvitationSendResult.Delivered(invitationId);
        }

        if (deliveryResult.FailureCode == "DevelopmentEmailSuppressed")
        {
            return AssessmentInvitationSendResult.EmailSuppressed(
                invitationId,
                deliveryResult.FailureCode,
                deliveryResult.FailureMessage ?? "Email delivery was suppressed in the development environment.");
        }

        return AssessmentInvitationSendResult.DeliveryFailed(
            invitationId,
            deliveryResult.FailureCode ?? "EmailDeliveryFailed",
            deliveryResult.FailureMessage ?? "Invitation delivery failed. Please retry or contact support.");
    }
}
