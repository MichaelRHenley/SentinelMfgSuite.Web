using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services;

namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed class AssessmentInvitationDeliveryService : IAssessmentInvitationDeliveryService
{
    private readonly ApplicationDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly IConfiguration _configuration;

    public AssessmentInvitationDeliveryService(ApplicationDbContext db, IEmailSender emailSender, IConfiguration configuration)
    {
        _db = db;
        _emailSender = emailSender;
        _configuration = configuration;
    }

    public async Task<AssessmentInvitationDeliveryResult> DeliverAsync(AssessmentInvitationDeliveryRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return AssessmentInvitationDeliveryResult.Failure("InvalidRequest", "Invitation delivery request is required.");
        }

        var appBaseUrl = _configuration["AppUrls:Base"];
        if (string.IsNullOrWhiteSpace(appBaseUrl))
        {
            return AssessmentInvitationDeliveryResult.Failure("MissingPublicBaseUrl", "A public application base URL is required for invitation delivery.");
        }

        if (!Uri.TryCreate(appBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            return AssessmentInvitationDeliveryResult.Failure("InvalidPublicBaseUrl", "The configured public application base URL is invalid.");
        }

        var nowUtc = DateTime.UtcNow;

        var claimRowsUpdated = await _db.Set<AssessmentInvitation>()
            .Where(i => i.Id == request.InvitationId
                && i.AssessmentId == request.AssessmentId
                && i.Email == request.RecipientEmail
                && (i.DeliveryStatus == AssessmentInvitationDeliveryStatus.Pending || i.DeliveryStatus == AssessmentInvitationDeliveryStatus.Failed)
                && i.RevokedUtc == null
                && i.ExpiresUtc > nowUtc
                && _db.Set<Assessment>().Any(a => a.Id == i.AssessmentId
                    && (a.Status == AssessmentStatus.Invited
                        || a.Status == AssessmentStatus.InProgress
                        || a.Status == AssessmentStatus.Submitted
                        || a.Status == AssessmentStatus.Completed)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.DeliveryStatus, AssessmentInvitationDeliveryStatus.Sending)
                .SetProperty(i => i.DeliveryAttemptCount, i => i.DeliveryAttemptCount + 1)
                .SetProperty(i => i.DeliveryLastAttemptUtc, nowUtc)
                .SetProperty(i => i.DeliveryFailureCode, (string?)null), ct);

        if (claimRowsUpdated != 1)
        {
            return await TranslateClaimFailureAsync(request.AssessmentId, request.InvitationId, nowUtc, ct);
        }

        var invitationUrl = AssessmentInvitationUrlBuilder.Build(
            appBaseUrl,
            request.InvitationId,
            request.RawInvitationToken);

        var encodedInvitationUrl = WebUtility.HtmlEncode(invitationUrl);
        var encodedExpiration = WebUtility.HtmlEncode(request.ExpiresUtc.ToString("yyyy-MM-dd HH:mm 'UTC'"));

        const string subject = "Sentinel AI Systems | Industrial Data & AI Readiness Assessment Invitation";
        var htmlBody = $"""
            <p>Hello,</p>
            <p>
                You are invited to complete the <strong>Industrial Data &amp; AI Readiness Assessment</strong>
                from <strong>Sentinel AI Systems</strong>.
            </p>
            <p>
                This questionnaire helps identify your current industrial data posture, integration priorities,
                and practical AI readiness opportunities.
            </p>
            <p>
                Start your assessment using this secure link:<br />
                <a href=\"{encodedInvitationUrl}\">{encodedInvitationUrl}</a>
            </p>
            <p>
                This invitation expires on <strong>{encodedExpiration}</strong>.
            </p>
            <p>
                Regards,<br />
                Sentinel AI Systems
            </p>
            """;

        try
        {
            var sendResult = await _emailSender.SendAsync(request.RecipientEmail, subject, htmlBody);

            if (sendResult.Outcome == EmailSendOutcome.Suppressed)
            {
                var suppressedRowsUpdated = await _db.Set<AssessmentInvitation>()
                    .Where(i => i.Id == request.InvitationId && i.DeliveryStatus == AssessmentInvitationDeliveryStatus.Sending)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(i => i.DeliveryStatus, AssessmentInvitationDeliveryStatus.Pending)
                        .SetProperty(i => i.DeliveryFailureCode, "DevEmailSuppressed"), ct);

                if (suppressedRowsUpdated != 1)
                {
                    return AssessmentInvitationDeliveryResult.Failure("DeliveryOutcomeUnknown", "Invitation delivery outcome is uncertain and requires explicit recovery handling.");
                }

                var isDevelopmentEmailSuppressed = _emailSender is DevelopmentSuppressingEmailSender;
                return AssessmentInvitationDeliveryResult.Failure(
                    isDevelopmentEmailSuppressed ? "DevelopmentEmailSuppressed" : "EmailSuppressed",
                    isDevelopmentEmailSuppressed
                        ? "Email delivery was suppressed in the development environment."
                        : "Email delivery was suppressed.",
                    isDevelopmentEmailSuppressed);
            }

            await _db.Set<AssessmentInvitation>()
                .Where(i => i.Id == request.InvitationId && i.DeliveryStatus == AssessmentInvitationDeliveryStatus.Sending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(i => i.DeliveryStatus, AssessmentInvitationDeliveryStatus.Accepted)
                    .SetProperty(i => i.DeliveryAcceptedUtc, DateTime.UtcNow)
                    .SetProperty(i => i.DeliveryFailureCode, (string?)null), ct);

            return AssessmentInvitationDeliveryResult.Success();
        }
        catch
        {
            await _db.Set<AssessmentInvitation>()
                .Where(i => i.Id == request.InvitationId && i.DeliveryStatus == AssessmentInvitationDeliveryStatus.Sending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(i => i.DeliveryStatus, AssessmentInvitationDeliveryStatus.Failed)
                    .SetProperty(i => i.DeliveryFailureCode, "EmailProviderFailure"), ct);

            return AssessmentInvitationDeliveryResult.Failure("EmailDeliveryFailed", "Invitation delivery failed. Please retry or contact support.");
        }
    }

    private async Task<AssessmentInvitationDeliveryResult> TranslateClaimFailureAsync(Guid assessmentId, Guid invitationId, DateTime nowUtc, CancellationToken ct)
    {
        var invitation = await _db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == invitationId && i.AssessmentId == assessmentId, ct);

        if (invitation is null)
        {
            return AssessmentInvitationDeliveryResult.Failure("InvitationNotFound", "Invitation was not found.");
        }

        if (invitation.RevokedUtc.HasValue)
        {
            return AssessmentInvitationDeliveryResult.Failure("InvitationRevoked", "Invitation has been revoked.");
        }

        if (invitation.ExpiresUtc <= nowUtc)
        {
            return AssessmentInvitationDeliveryResult.Failure("InvitationExpired", "Invitation has expired.");
        }

        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == invitation.AssessmentId, ct);

        if (assessment is null || !IsEligibleAssessmentStatus(assessment.Status))
        {
            return AssessmentInvitationDeliveryResult.Failure("IneligibleAssessment", "Associated assessment is not eligible for delivery.");
        }

        if (invitation.DeliveryStatus == AssessmentInvitationDeliveryStatus.OutcomeUnknown)
        {
            return AssessmentInvitationDeliveryResult.Failure("DeliveryOutcomeUnknown", "Invitation delivery outcome is uncertain and requires explicit recovery handling.");
        }

        return AssessmentInvitationDeliveryResult.Failure("DeliveryAlreadyClaimed", "Invitation delivery is already in progress or completed.");
    }

    private static bool IsEligibleAssessmentStatus(AssessmentStatus status)
        => status is AssessmentStatus.Invited
            or AssessmentStatus.InProgress
            or AssessmentStatus.Submitted
            or AssessmentStatus.Completed;

}
