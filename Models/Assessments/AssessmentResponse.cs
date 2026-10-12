using System;

namespace SentinelMfgSuite.Web.Models.Assessments;

public class AssessmentResponse
{
    public Guid Id { get; set; }

    public Guid AssessmentId { get; set; }

    public Guid InvitationId { get; set; }

    // Stored as PostgreSQL jsonb
    public string Data { get; set; } = "{}";

    public bool IsDraft { get; set; }

    public DateTime LastSavedUtc { get; set; }
    public DateTime? SubmittedUtc { get; set; }

    // Concurrency token for repeated saves
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();
}
