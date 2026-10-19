using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed class AssessmentInvitationIssuanceService : IAssessmentInvitationIssuanceService, IAssessmentInvitationIssuanceTransactionParticipant
{
    private readonly ApplicationDbContext _db;
    private readonly Func<string> _tokenFactory;

    public AssessmentInvitationIssuanceService(ApplicationDbContext db, Func<string>? tokenFactory = null)
    {
        _db = db;
        _tokenFactory = tokenFactory ?? GenerateToken;
    }

    public async Task<AssessmentInvitationIssuanceResult> IssueAsync(Guid assessmentId, TimeSpan expiresIn, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            var result = await IssueWithinExistingTransactionAsync(assessmentId, expiresIn, ct);

            if (!result.Succeeded)
            {
                await tx.RollbackAsync(ct);
                return result;
            }

            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    async Task<AssessmentInvitationIssuanceResult> IAssessmentInvitationIssuanceTransactionParticipant.IssueWithinExistingTransactionAsync(Guid assessmentId, TimeSpan expiresIn, CancellationToken ct)
        => await IssueWithinExistingTransactionAsync(assessmentId, expiresIn, ct);

    internal async Task<AssessmentInvitationIssuanceResult> IssueWithinExistingTransactionAsync(Guid assessmentId, TimeSpan expiresIn, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            return AssessmentInvitationIssuanceResult.Failure("MissingTransaction", "An active database transaction is required for transaction-participating issuance.");
        }

        if (assessmentId == Guid.Empty)
        {
            return AssessmentInvitationIssuanceResult.Failure("InvalidAssessmentId", "Assessment id is required.");
        }

        if (expiresIn <= TimeSpan.Zero)
        {
            return AssessmentInvitationIssuanceResult.Failure("InvalidExpirationPeriod", "Invitation expiration period must be greater than zero.");
        }

        var nowUtc = DateTime.UtcNow;

        var assessmentRowsUpdated = await _db.Set<Assessment>()
            .Where(x => x.Id == assessmentId
                && x.Status == AssessmentStatus.Draft
                && !_db.Set<AssessmentInvitation>().Any(i => i.AssessmentId == x.Id
                    && i.RevokedUtc == null
                    && i.ExpiresUtc > nowUtc))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, AssessmentStatus.Invited)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);

        if (assessmentRowsUpdated != 1)
        {
            var assessment = await _db.Set<Assessment>()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == assessmentId, ct);

            if (assessment is null)
            {
                return AssessmentInvitationIssuanceResult.Failure("AssessmentNotFound", "Assessment was not found.");
            }

            var hasActiveInvitation = await _db.Set<AssessmentInvitation>()
                .AsNoTracking()
                .AnyAsync(x => x.AssessmentId == assessmentId
                    && x.RevokedUtc == null
                    && x.ExpiresUtc > nowUtc, ct);

            if (hasActiveInvitation)
            {
                return AssessmentInvitationIssuanceResult.Failure("DuplicateActiveInvitation", "An active invitation already exists for this assessment.");
            }

            return AssessmentInvitationIssuanceResult.Failure("IneligibleAssessmentStatus", "Assessment status is not eligible for invitation issuance.");
        }

        var assessmentForInvitation = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == assessmentId, ct);

        if (assessmentForInvitation is null)
        {
            return AssessmentInvitationIssuanceResult.Failure("AssessmentNotFound", "Assessment was not found.");
        }

        if (string.IsNullOrWhiteSpace(assessmentForInvitation.ContactEmail))
        {
            return AssessmentInvitationIssuanceResult.Failure("MissingContactEmail", "Assessment contact email is required for invitation issuance.");
        }

        var token = _tokenFactory();
        var tokenHash = AssessmentInvitationAccessService.ComputeTokenHash(token);
        var expiresUtc = nowUtc.Add(expiresIn);

        var invitation = new AssessmentInvitation
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessmentId,
            Email = assessmentForInvitation.ContactEmail.Trim(),
            TokenHash = tokenHash,
            CreatedUtc = nowUtc,
            ExpiresUtc = expiresUtc,
            RevokedUtc = null,
            RevokedByUserId = null,
            LastAccessUtc = null,
            AccessCount = 0,
            CreatedByUserId = null
        };

        _db.Set<AssessmentInvitation>().Add(invitation);
        await _db.SaveChangesAsync(ct);

        return AssessmentInvitationIssuanceResult.Success(invitation.Id, token, expiresUtc);
    }

    public async Task<AssessmentInvitationReplacementIssuanceResult> ReplaceAsync(Guid assessmentId, Guid existingInvitationId, TimeSpan expiresIn, CancellationToken ct = default)
    {
        if (assessmentId == Guid.Empty || existingInvitationId == Guid.Empty)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("InvalidRequest", "A valid assessment and invitation are required.");
        }

        if (expiresIn <= TimeSpan.Zero)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("InvalidExpirationPeriod", "Invitation expiration period must be greater than zero.");
        }

        var nowUtc = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var revokedRows = await _db.Set<AssessmentInvitation>()
            .Where(i => i.Id == existingInvitationId
                && i.AssessmentId == assessmentId
                && i.RevokedUtc == null
                && (i.DeliveryStatus == AssessmentInvitationDeliveryStatus.Pending || i.DeliveryStatus == AssessmentInvitationDeliveryStatus.Failed)
                && i.AccessCount == 0
                && i.LastAccessUtc == null
                && !_db.Set<AssessmentResponse>().Any(r => r.InvitationId == i.Id)
                && _db.Set<Assessment>().Any(a => a.Id == assessmentId && a.Status == AssessmentStatus.Invited)
                && !_db.Set<AssessmentInvitation>().Any(other => other.AssessmentId == assessmentId
                    && other.Id != i.Id
                    && other.RevokedUtc == null
                    && other.ExpiresUtc > nowUtc))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.RevokedUtc, nowUtc)
                .SetProperty(i => i.RevokedByUserId, (string?)null), ct);

        if (revokedRows != 1)
        {
            var failure = await TranslateReplacementFailureAsync(assessmentId, existingInvitationId, nowUtc, ct);
            await tx.RollbackAsync(ct);
            return failure;
        }

        var existingInvitation = await _db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == existingInvitationId && i.AssessmentId == assessmentId, ct);

        if (existingInvitation is null)
        {
            await tx.RollbackAsync(ct);
            return AssessmentInvitationReplacementIssuanceResult.Failure("InvitationNotFound", "Invitation was not found.");
        }

        var token = _tokenFactory();
        var tokenHash = AssessmentInvitationAccessService.ComputeTokenHash(token);
        var expiresUtc = nowUtc.Add(expiresIn);

        var replacementInvitation = new AssessmentInvitation
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessmentId,
            Email = existingInvitation.Email,
            TokenHash = tokenHash,
            CreatedUtc = nowUtc,
            ExpiresUtc = expiresUtc,
            RevokedUtc = null,
            RevokedByUserId = null,
            LastAccessUtc = null,
            AccessCount = 0,
            DeliveryStatus = AssessmentInvitationDeliveryStatus.Pending,
            DeliveryAttemptCount = 0,
            DeliveryLastAttemptUtc = null,
            DeliveryAcceptedUtc = null,
            DeliveryFailureCode = null,
            CreatedByUserId = null
        };

        _db.Set<AssessmentInvitation>().Add(replacementInvitation);

        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AssessmentInvitationReplacementIssuanceResult.Success(replacementInvitation.Id, token, expiresUtc);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            return AssessmentInvitationReplacementIssuanceResult.Failure("ReplacementFailed", "Invitation replacement failed due to a concurrency conflict or persistence error.");
        }
    }

    private async Task<AssessmentInvitationReplacementIssuanceResult> TranslateReplacementFailureAsync(Guid assessmentId, Guid invitationId, DateTime nowUtc, CancellationToken ct)
    {
        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == assessmentId, ct);

        if (assessment is null)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("AssessmentNotFound", "Assessment was not found.");
        }

        if (assessment.Status != AssessmentStatus.Invited)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("IneligibleAssessmentStatus", "Assessment is not eligible for invitation replacement.");
        }

        var invitation = await _db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == invitationId, ct);

        if (invitation is null)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("InvitationNotFound", "Invitation was not found.");
        }

        if (invitation.AssessmentId != assessmentId)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("InvitationAssessmentMismatch", "Invitation is not eligible for replacement.");
        }

        if (invitation.RevokedUtc.HasValue)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("InvitationRevoked", "Invitation is already revoked.");
        }

        if (invitation.DeliveryStatus is AssessmentInvitationDeliveryStatus.Sending
            or AssessmentInvitationDeliveryStatus.OutcomeUnknown
            or AssessmentInvitationDeliveryStatus.Accepted)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("IneligibleDeliveryStatus", "Invitation delivery status is not eligible for replacement.");
        }

        if (invitation.DeliveryStatus is not (AssessmentInvitationDeliveryStatus.Pending or AssessmentInvitationDeliveryStatus.Failed))
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("IneligibleDeliveryStatus", "Invitation delivery status is not eligible for replacement.");
        }

        if (invitation.AccessCount > 0 || invitation.LastAccessUtc.HasValue)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("InvitationAlreadyUsed", "Invitation is not eligible for replacement.");
        }

        var hasResponse = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .AnyAsync(r => r.InvitationId == invitation.Id, ct);

        if (hasResponse)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("InvitationAlreadyUsed", "Invitation is not eligible for replacement.");
        }

        var hasOtherActiveInvitation = await _db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .AnyAsync(other => other.AssessmentId == assessmentId
                && other.Id != invitation.Id
                && other.RevokedUtc == null
                && other.ExpiresUtc > nowUtc, ct);

        if (hasOtherActiveInvitation)
        {
            return AssessmentInvitationReplacementIssuanceResult.Failure("AnotherActiveInvitationExists", "Assessment already has another active invitation.");
        }

        return AssessmentInvitationReplacementIssuanceResult.Failure("ReplacementConflict", "Invitation replacement could not be completed due to a concurrent change.");
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
