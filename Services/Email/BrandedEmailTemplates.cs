using System.Net;
using System.Text;

namespace SentinelMfgSuite.Web.Services.Email
{
    public enum EmailTemplateKind
    {
        AssessmentInvitation = 1,
        SubmissionConfirmation = 2,
        ConsultantSubmissionNotification = 3,
        PaymentConfirmation = 4,
        ReportReady = 5,
        CorrectedReportNotification = 6
    }

    public sealed record RenderedEmail(string Subject, string HtmlBody, string TextBody, string Classification);

    public sealed record EmailContentModel(
        string Subject,
        string Heading,
        IReadOnlyList<string> Paragraphs,
        string? CallToActionLabel = null,
        string? CallToActionUrl = null);

    public static class BrandedEmailTemplates
    {
        public const string BrandName = "Sentinel AI Systems";
        public const string SupportAddress = "support@sentinelaisystems.net";

        /// <summary>Payment and report-delivery templates stay inactive until those workflows exist.</summary>
        public static bool IsActive(EmailTemplateKind kind) => kind is
            EmailTemplateKind.AssessmentInvitation
            or EmailTemplateKind.SubmissionConfirmation
            or EmailTemplateKind.ConsultantSubmissionNotification;

        public static RenderedEmail AssessmentInvitation(string invitationUrl, DateTime expiresUtc)
            => Render(EmailTemplateKind.AssessmentInvitation, new EmailContentModel(
                "Sentinel AI Systems | Industrial Data & AI Readiness Assessment Invitation",
                "Your Industrial Data & AI Readiness Assessment",
                new[]
                {
                    "You are invited to complete the Industrial Data & AI Readiness Assessment from Sentinel AI Systems.",
                    "This questionnaire helps identify your current industrial data posture, integration priorities, and practical AI readiness opportunities.",
                    $"This invitation expires on {expiresUtc:yyyy-MM-dd HH:mm} UTC."
                },
                "Start your assessment",
                invitationUrl));

        public static RenderedEmail SubmissionConfirmation()
            => Render(EmailTemplateKind.SubmissionConfirmation, new EmailContentModel(
                "Sentinel AI Systems | We received your assessment",
                "Thank you for submitting your assessment",
                new[]
                {
                    "We have received your completed Industrial Data & AI Readiness Assessment.",
                    "A Sentinel AI Systems consultant will review your responses and contact you about next steps.",
                    $"If you have questions, reply to this email or contact {SupportAddress}."
                }));

        public static RenderedEmail ConsultantSubmissionNotification(Guid assessmentId, string? siteName, string? title, DateTime submittedUtc)
            => Render(EmailTemplateKind.ConsultantSubmissionNotification, new EmailContentModel(
                "Sentinel | Assessment submitted",
                "An assessment has been submitted",
                new[]
                {
                    $"Assessment: {(string.IsNullOrWhiteSpace(title) ? assessmentId.ToString() : title)}",
                    $"Site: {(string.IsNullOrWhiteSpace(siteName) ? "n/a" : siteName)}",
                    $"Submitted: {submittedUtc:yyyy-MM-dd HH:mm} UTC",
                    $"Assessment id: {assessmentId}"
                }));

        // Inactive templates: structural placeholders only. No payment details or report links are invented.
        public static RenderedEmail PaymentConfirmation()
            => RenderInactive(EmailTemplateKind.PaymentConfirmation, "Payment confirmation");

        public static RenderedEmail ReportReady()
            => RenderInactive(EmailTemplateKind.ReportReady, "Your report is ready");

        public static RenderedEmail CorrectedReportNotification()
            => RenderInactive(EmailTemplateKind.CorrectedReportNotification, "A corrected report is available");

        private static RenderedEmail RenderInactive(EmailTemplateKind kind, string heading)
            => Render(kind, new EmailContentModel(
                $"Sentinel AI Systems | {heading}",
                heading,
                new[] { "This message will be available once the corresponding workflow is enabled." }));

        public static RenderedEmail Render(EmailTemplateKind kind, EmailContentModel model)
        {
            static string enc(string s) => WebUtility.HtmlEncode(s);
            var html = new StringBuilder();
            html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" />");
            html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
            html.Append($"<title>{enc(model.Subject)}</title></head>");
            html.Append("<body style=\"margin:0;padding:0;background:#f3f5f8;font-family:Segoe UI,Arial,sans-serif;color:#1f2933;\">");
            html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f3f5f8;padding:24px 12px;\"><tr><td align=\"center\">");
            html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;background:#ffffff;border-radius:6px;overflow:hidden;\">");
            html.Append($"<tr><td style=\"background:#0b2545;color:#ffffff;padding:20px 28px;font-size:18px;font-weight:600;\">{BrandName}</td></tr>");
            html.Append("<tr><td style=\"padding:28px;font-size:16px;line-height:1.5;\">");
            html.Append($"<h1 style=\"margin:0 0 16px;font-size:22px;color:#0b2545;\">{enc(model.Heading)}</h1>");
            foreach (var p in model.Paragraphs)
            {
                html.Append($"<p style=\"margin:0 0 16px;\">{enc(p)}</p>");
            }
            if (model.CallToActionUrl is not null && model.CallToActionLabel is not null)
            {
                html.Append($"<p style=\"margin:24px 0;\"><a href=\"{enc(model.CallToActionUrl)}\" style=\"background:#1d6fd8;color:#ffffff;text-decoration:none;padding:12px 24px;border-radius:4px;display:inline-block;font-weight:600;\">{enc(model.CallToActionLabel)}</a></p>");
                html.Append($"<p style=\"margin:0 0 16px;font-size:13px;color:#52606d;\">If the button does not work, copy this link into your browser:<br />{enc(model.CallToActionUrl)}</p>");
            }
            html.Append("</td></tr>");
            html.Append($"<tr><td style=\"padding:20px 28px;background:#f3f5f8;font-size:12px;color:#52606d;\">{BrandName}<br />Questions? Reply to this email or contact {SupportAddress}.</td></tr>");
            html.Append("</table></td></tr></table></body></html>");

            var text = new StringBuilder();
            text.AppendLine(BrandName).AppendLine().AppendLine(model.Heading).AppendLine();
            foreach (var p in model.Paragraphs)
            {
                text.AppendLine(p).AppendLine();
            }
            if (model.CallToActionUrl is not null && model.CallToActionLabel is not null)
            {
                text.AppendLine($"{model.CallToActionLabel}: {model.CallToActionUrl}").AppendLine();
            }
            text.AppendLine("--").AppendLine(BrandName).AppendLine($"Questions? Reply to this email or contact {SupportAddress}.");

            return new RenderedEmail(model.Subject, html.ToString(), text.ToString(), kind.ToString());
        }
    }
}
