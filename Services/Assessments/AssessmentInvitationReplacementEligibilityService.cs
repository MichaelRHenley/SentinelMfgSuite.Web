using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed class AssessmentInvitationReplacementEligibilityService : IAssessmentInvitationReplacementEligibilityService
{
    private readonly ApplicationDbContext _db;

    public AssessmentInvitationReplacementEligibilityService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<AssessmentInvitationReplacementEligibilityResult> EvaluateAsync(Guid assessmentId, Guid invitationId, CancellationToken ct = default)
    {
        if (assessmentId == Guid.Empty || invitationId == Guid.Empty)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "InvalidRequest",
                "A valid assessment and invitation are required.");
        }

        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == assessmentId, ct);

        if (assessment is null)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "AssessmentNotFound",
                "Assessment was not found.");
        }

        if (assessment.Status != AssessmentStatus.Invited)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "IneligibleAssessmentStatus",
                "Assessment is not eligible for invitation replacement.");
        }

        var invitation = await _db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invitationId, ct);

        if (invitation is null)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "InvitationNotFound",
                "Invitation was not found.");
        }

        if (invitation.AssessmentId != assessmentId)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "InvitationAssessmentMismatch",
                "Invitation is not eligible for replacement.");
        }

        if (invitation.RevokedUtc.HasValue)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "InvitationRevoked",
                "Invitation is already revoked.");
        }

        if (invitation.DeliveryStatus is AssessmentInvitationDeliveryStatus.Sending
            or AssessmentInvitationDeliveryStatus.OutcomeUnknown
            or AssessmentInvitationDeliveryStatus.Accepted)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "IneligibleDeliveryStatus",
                "Invitation delivery status is not eligible for replacement.");
        }

        if (invitation.DeliveryStatus is not (AssessmentInvitationDeliveryStatus.Pending or AssessmentInvitationDeliveryStatus.Failed))
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "IneligibleDeliveryStatus",
                "Invitation delivery status is not eligible for replacement.");
        }

        if (invitation.AccessCount > 0 || invitation.LastAccessUtc.HasValue)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "InvitationAlreadyUsed",
                "Invitation is not eligible for replacement.");
        }

        var hasResponse = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .AnyAsync(x => x.InvitationId == invitationId, ct);

        if (hasResponse)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "InvitationAlreadyUsed",
                "Invitation is not eligible for replacement.");
        }

        var hasOtherActiveInvitation = await _db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .AnyAsync(x => x.AssessmentId == assessmentId
                && x.Id != invitationId
                && x.RevokedUtc == null
                && x.ExpiresUtc > DateTime.UtcNow, ct);

        if (hasOtherActiveInvitation)
        {
            return AssessmentInvitationReplacementEligibilityResult.Ineligible(
                "AnotherActiveInvitationExists",
                "Assessment already has another active invitation.");
        }

        return AssessmentInvitationReplacementEligibilityResult.Eligible();
    }
}
