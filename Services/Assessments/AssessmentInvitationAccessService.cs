using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed class AssessmentInvitationAccessService : IAssessmentInvitationAccessService
{
    private const int MaxTokenLength = 512;
    private readonly ApplicationDbContext _db;

    public AssessmentInvitationAccessService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<AssessmentInvitationAccessResult> ValidateAccessAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default)
    {
        if (invitationId == Guid.Empty || string.IsNullOrWhiteSpace(invitationToken) || invitationToken.Length > MaxTokenLength)
        {
            return AssessmentInvitationAccessResult.Denied();
        }

        var invitation = await _db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invitationId, ct);

        if (invitation is null)
        {
            return AssessmentInvitationAccessResult.Denied();
        }

        var suppliedTokenHash = ComputeTokenHash(invitationToken);
        var storedTokenHash = NormalizeTokenHash(invitation.TokenHash);

        if (storedTokenHash is null || !FixedTimeEquals(storedTokenHash, suppliedTokenHash))
        {
            return AssessmentInvitationAccessResult.Denied();
        }

        var nowUtc = DateTime.UtcNow;

        if (invitation.RevokedUtc.HasValue)
        {
            return AssessmentInvitationAccessResult.Revoked();
        }

        if (invitation.ExpiresUtc <= nowUtc)
        {
            return AssessmentInvitationAccessResult.Expired();
        }

        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invitation.AssessmentId, ct);

        if (assessment is null || !IsEligibleAssessmentStatus(assessment.Status))
        {
            return AssessmentInvitationAccessResult.Ineligible();
        }

        var response = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.InvitationId == invitation.Id, ct);

        if (response is not null && response.AssessmentId != invitation.AssessmentId)
        {
            return AssessmentInvitationAccessResult.Ineligible();
        }

        var responseState = DetermineResponseState(response);
        var isReadOnly = responseState == AssessmentInvitationResponseState.Submitted
            || assessment.Status is AssessmentStatus.Submitted or AssessmentStatus.Completed;

        return AssessmentInvitationAccessResult.Authorized(
            invitation.AssessmentId,
            invitation.Id,
            response?.Id,
            responseState,
            isReadOnly);
    }

    internal static string ComputeTokenHash(string token)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string? NormalizeTokenHash(string? tokenHash)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            return null;
        }

        return tokenHash.Trim().ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsEligibleAssessmentStatus(AssessmentStatus status)
        => status is AssessmentStatus.Invited
            or AssessmentStatus.InProgress
            or AssessmentStatus.Submitted
            or AssessmentStatus.Completed;

    private static AssessmentInvitationResponseState DetermineResponseState(AssessmentResponse? response)
    {
        if (response is null)
        {
            return AssessmentInvitationResponseState.NotStarted;
        }

        return response.SubmittedUtc.HasValue || !response.IsDraft
            ? AssessmentInvitationResponseState.Submitted
            : AssessmentInvitationResponseState.Draft;
    }
}
