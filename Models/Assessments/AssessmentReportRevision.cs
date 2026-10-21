namespace SentinelMfgSuite.Web.Models.Assessments;

public enum AssessmentReportRevisionStatus : short
{
    Draft = 0,
    InReview = 1,
    Approved = 2,
    Issued = 3,
    Superseded = 4,
    Rejected = 5
}

public sealed class AssessmentReportRevision
{
    public Guid Id { get; set; }

    public Guid AssessmentReportId { get; set; }

    public int RevisionNumber { get; set; }

    public AssessmentReportRevisionStatus Status { get; set; } = AssessmentReportRevisionStatus.Draft;

    public string ExecutiveSummary { get; set; } = string.Empty;

    public string ScopeAndMethodology { get; set; } = string.Empty;

    public string AssumptionsAndLimitations { get; set; } = string.Empty;

    public string NextSteps { get; set; } = string.Empty;

    public string? ScoringContractVersion { get; set; }

    public int? ScoringSnapshotSchemaVersion { get; set; }

    public string? ScoringSnapshot { get; set; }

    public string? ConsultantContentJson { get; set; }

    public string? ConsultantContentSchemaVersion { get; set; }

    public string? ReportTemplateVersion { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;

    public string UpdatedByUserId { get; set; } = string.Empty;

    public DateTime? SubmittedForReviewUtc { get; set; }

    public string? SubmittedForReviewByUserId { get; set; }

    public DateTime? ApprovedUtc { get; set; }

    public string? ApprovedByUserId { get; set; }

    public DateTime? IssuedUtc { get; set; }

    public string? IssuedByUserId { get; set; }

    /// <summary>
    /// The revision that replaced this one. Set only when <see cref="Status"/> is Superseded
    /// and always refers to a revision of the same report.
    /// </summary>
    public Guid? SupersededByRevisionId { get; set; }

    public string? PdfStorageKey { get; set; }

    public string? PdfSha256 { get; set; }

    public long? PdfLengthBytes { get; set; }

    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    public AssessmentReport AssessmentReport { get; set; } = null!;
}
