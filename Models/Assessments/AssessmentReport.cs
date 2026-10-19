namespace SentinelMfgSuite.Web.Models.Assessments;

public sealed class AssessmentReport
{
    public Guid Id { get; set; }

    public Guid AssessmentId { get; set; }

    public Guid SubmittedResponseId { get; set; }

    public string QuestionnaireVersion { get; set; } = string.Empty;

    public DateTime ResponseSubmittedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;

    public string UpdatedByUserId { get; set; } = string.Empty;

    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    public Assessment Assessment { get; set; } = null!;

    public AssessmentResponse SubmittedResponse { get; set; } = null!;

    public ICollection<AssessmentReportRevision> Revisions { get; set; } = new List<AssessmentReportRevision>();
}
