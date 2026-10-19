using System;

namespace SentinelMfgSuite.Web.Services.Assessments;

public static class AssessmentInvitationUrlBuilder
{
    public static string Build(string appBaseUrl, Guid invitationId, string rawInvitationToken)
    {
        if (!Uri.TryCreate(appBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("A valid application base URL is required.", nameof(appBaseUrl));
        }

        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException("Invitation id is required.", nameof(invitationId));
        }

        if (string.IsNullOrWhiteSpace(rawInvitationToken))
        {
            throw new ArgumentException("Invitation token is required.", nameof(rawInvitationToken));
        }

        var urlBuilder = new UriBuilder(baseUri)
        {
            Path = CombinePath(baseUri.AbsolutePath, $"assessment/questionnaire/{invitationId:D}"),
            Query = $"token={Uri.EscapeDataString(rawInvitationToken)}"
        };

        return urlBuilder.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
    }

    private static string CombinePath(string basePath, string relativePath)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(basePath) || basePath == "/"
            ? string.Empty
            : basePath.TrimEnd('/');

        var normalizedRelative = relativePath.TrimStart('/');
        return $"{normalizedBase}/{normalizedRelative}";
    }
}