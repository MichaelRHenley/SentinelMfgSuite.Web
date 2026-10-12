using System;

namespace SentinelMfgSuite.Web.Models.Assessments;

public class AssessmentInvitation
{
    public Guid Id { get; set; }

    public Guid AssessmentId { get; set; }

    public string Email { get; set; } = default!;

    // Store only the secure hash of the token (e.g. SHA256 hex)
    public string TokenHash { get; set; } = default!;

    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }
    public string? RevokedByUserId { get; set; }

    public DateTime? LastAccessUtc { get; set; }
    public int AccessCount { get; set; }

    public string? CreatedByUserId { get; set; }
}
