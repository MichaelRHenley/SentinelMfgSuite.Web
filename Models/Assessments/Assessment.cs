using System;

namespace SentinelMfgSuite.Web.Models.Assessments;

public enum AssessmentStatus : short
{
    Draft = 0,
    Invited = 1,
    InProgress = 2,
    Submitted = 3,
    Completed = 4,
    Cancelled = 5
}

public class Assessment
{
    public Guid Id { get; set; }

    public string? Title { get; set; }
    public string? SiteName { get; set; }
    public string? ContactEmail { get; set; }

    public AssessmentStatus Status { get; set; } = AssessmentStatus.Draft;

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public string? Notes { get; set; }

    // Optional owner (admin) who created the assessment
    public string? OwnerUserId { get; set; }

    // Questionnaire schema version bound to this assessment (e.g. "1.0")
    public string QuestionnaireVersion { get; set; } = "1.0";
}
