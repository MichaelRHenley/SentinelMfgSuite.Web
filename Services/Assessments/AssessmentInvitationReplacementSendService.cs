using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments;

/// <summary>
/// Orchestrates replacing an eligible, unused invitation for an existing assessment.
/// Delegates the atomic revoke-and-reissue transaction to
/// AssessmentInvitationIssuanceService.ReplaceAsync and only attempts delivery after
/// that transaction commits. Never creates another assessment, never reimplements
/// token generation or transactional replacement, and never logs or persists the raw token.
/// </summary>
public sealed class AssessmentInvitationReplacementSendService : IAssessmentInvitationReplacementSendService
{
    public static readonly TimeSpan InvitationExpiration = TimeSpan.FromDays(7);

    private readonly ApplicationDbContext _db;
    private readonly IAssessmentInvitationIssuanceService _issuanceService;
    private readonly IAssessmentInvitationDeliveryService _deliveryService;

    public AssessmentInvitationReplacementSendService(
        ApplicationDbContext db,
        IAssessmentInvitationIssuanceService issuanceService,
        IAssessmentInvitationDeliveryService deliveryService)
    {
        _db = db;
        _issuanceService = issuanceService;
        _deliveryService = deliveryService;
    }

    public async Task<AssessmentInvitationReplacementSendResult> ReplaceAndSendAsync(
        Guid assessmentId,
        Guid existingInvitationId,
        CancellationToken ct = default,
        Action<Guid, string>? onDevelopmentEmailSuppressed = null)
    {
        if (assessmentId == Guid.Empty || existingInvitationId == Guid.Empty)
        {
            return AssessmentInvitationReplacementSendResult.ReplacementFailed(
                "InvalidRequest",
                "A valid assessment and invitation are required.");
        }

        // ReplaceAsync atomically revokes the existing invitation and issues a
        // replacement within a single transaction, committing only on success.
        // This is the sole authority for eligibility/concurrency at POST time.
        var replacement = await _issuanceService.ReplaceAsync(assessmentId, existingInvitationId, InvitationExpiration, ct);

        if (!replacement.Succeeded
            || replacement.InvitationId is null
            || replacement.ExpiresUtc is null
            || string.IsNullOrWhiteSpace(replacement.InvitationToken))
        {
            return AssessmentInvitationReplacementSendResult.ReplacementFailed(
                replacement.FailureCode ?? "ReplacementFailed",
                replacement.FailureMessage ?? "Invitation could not be replaced.");
        }

        var invitationId = replacement.InvitationId.Value;
        var rawInvitationToken = replacement.InvitationToken;
        var expiresUtc = replacement.ExpiresUtc.Value;

        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == assessmentId, ct);

        if (assessment is null || string.IsNullOrWhiteSpace(assessment.ContactEmail))
        {
            // Replacement already committed; delivery cannot proceed without a recipient.
            // Records are preserved; this is an ambiguous state requiring manual investigation.
            return AssessmentInvitationReplacementSendResult.DeliveryFailed(
                invitationId,
                "MissingContactEmail",
                "The invitation was replaced, but no delivery could be attempted because the assessment contact email is unavailable.");
        }

        // Delivery is only attempted after the replacement transaction has committed.
        var deliveryRequest = new AssessmentInvitationDeliveryRequest(
            assessmentId,
            invitationId,
            assessment.ContactEmail,
            rawInvitationToken,
            expiresUtc);

        var deliveryResult = await _deliveryService.DeliverAsync(deliveryRequest, ct);

        if (deliveryResult.Succeeded)
        {
            return AssessmentInvitationReplacementSendResult.Delivered(invitationId);
        }

        if (deliveryResult.IsDevelopmentEmailSuppressed
            && deliveryResult.FailureCode == "DevelopmentEmailSuppressed")
        {
            onDevelopmentEmailSuppressed?.Invoke(invitationId, rawInvitationToken);

            return AssessmentInvitationReplacementSendResult.EmailSuppressed(
                invitationId,
                deliveryResult.FailureCode,
                deliveryResult.FailureMessage ?? "Email delivery was suppressed in the development environment.");
        }

        return AssessmentInvitationReplacementSendResult.DeliveryFailed(
            invitationId,
            deliveryResult.FailureCode ?? "EmailDeliveryFailed",
            deliveryResult.FailureMessage ?? "Invitation delivery failed. Please retry or contact support.");
    }
}
