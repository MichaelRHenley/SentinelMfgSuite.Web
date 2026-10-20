namespace SentinelMfgSuite.Web.Models.Assessments;

public enum AssessmentReportTransitionEventType
{
    Created = 1,
    Transition = 2
}

/// <summary>
/// Append-only audit record of an assessment report revision lifecycle event.
/// Rows are protected against UPDATE, DELETE and TRUNCATE by PostgreSQL triggers.
/// </summary>
public class AssessmentReportTransitionHistory
{
    public Guid Id { get; set; }
    public Guid AssessmentReportId { get; set; }
    public Guid AssessmentReportRevisionId { get; set; }
    public AssessmentReportTransitionEventType EventType { get; set; }
    public AssessmentReportRevisionStatus? PreviousStatus { get; set; }
    public AssessmentReportRevisionStatus NewStatus { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public DateTime OccurredUtc { get; set; }
    public string? Reason { get; set; }
    public Guid? ReplacedRevisionId { get; set; }
    public Guid? ReplacementRevisionId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}
