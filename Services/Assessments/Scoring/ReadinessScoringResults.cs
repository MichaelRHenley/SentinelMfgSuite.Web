using System.Collections.ObjectModel;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

namespace SentinelMfgSuite.Web.Services.Assessments.Scoring;

public enum ReadinessOverallStatus
{
    Eligible,
    Withheld,
    HistoricalNoScore,
    UnsupportedQuestionnaireVersion
}

public enum ReadinessDimensionStatus
{
    Eligible,
    Withheld
}

public enum ReadinessItemStatus
{
    Scoreable,
    Missing,
    Unknown,
    ReliabilityNotEstablished,
    UnmappedValue,
    InvalidAnswerShape,
    NotEvaluated
}

public enum ReadinessScoreReason
{
    HistoricalVersionNotScored,
    UnsupportedQuestionnaireVersion,
    SchemaVersionMismatch,
    NotScoredForV2,
    InsufficientScoreableItems,
    RequiredEvidenceGroupMissing,
    OverallNotAvailableForV2,
    OverallRequiresAllDimensions
}

public enum ReadinessMaturityClassification
{
    Foundational,
    Developing,
    Established,
    Advanced
}

public enum ReadinessEvidenceProvenance
{
    SelfReported
}

public sealed class ReadinessReviewFlag
{
    public string Code { get; }
    public string Severity { get; }
    public string Message { get; }
    public IReadOnlyList<string> QuestionIds { get; }
    public IReadOnlyList<string> AnswerValues { get; }

    private ReadinessReviewFlag(string code, string severity, string message, IEnumerable<string> questionIds, IEnumerable<string> answerValues)
    {
        Code = code;
        Severity = severity;
        Message = message;
        QuestionIds = new ReadOnlyCollection<string>(questionIds.ToArray());
        AnswerValues = new ReadOnlyCollection<string>(answerValues.ToArray());
    }

    internal static ReadinessReviewFlag Create(string code, string message, params (string QuestionId, string AnswerValue)[] evidence)
        => new(
            code,
            "Informational",
            message,
            evidence.Select(item => item.QuestionId),
            evidence.Select(item => item.AnswerValue));
}

public sealed record ReadinessItemResult(
    string QuestionId,
    string? FieldId,
    string ItemKey,
    string? RawValue,
    ReadinessItemStatus Status,
    int? Score0To4,
    double? NormalizedScore0To100,
    double? DimensionContribution,
    string? Reason);

public sealed class ReadinessDimensionResult
{
    public string DimensionId { get; }
    public string Name { get; }
    public decimal Weight { get; }
    public ReadinessDimensionStatus Status { get; }
    public double? RawScore0To100 { get; }
    public int? DisplayScore0To100 { get; }
    public ReadinessMaturityClassification? Classification { get; }
    public int ScoreableItemCount { get; }
    public int TotalEligibleItemCount { get; }
    public double? EvidenceCoverage { get; }
    public IReadOnlyList<ReadinessItemResult> Items { get; }
    public IReadOnlyList<ReadinessScoreReason> WithholdingReasons { get; }

    internal ReadinessDimensionResult(
        string dimensionId,
        string name,
        decimal weight,
        ReadinessDimensionStatus status,
        double? rawScore0To100,
        int? displayScore0To100,
        ReadinessMaturityClassification? classification,
        int scoreableItemCount,
        int totalEligibleItemCount,
        double? evidenceCoverage,
        IEnumerable<ReadinessItemResult> items,
        IEnumerable<ReadinessScoreReason> withholdingReasons)
    {
        DimensionId = dimensionId;
        Name = name;
        Weight = weight;
        Status = status;
        RawScore0To100 = rawScore0To100;
        DisplayScore0To100 = displayScore0To100;
        Classification = classification;
        ScoreableItemCount = scoreableItemCount;
        TotalEligibleItemCount = totalEligibleItemCount;
        EvidenceCoverage = evidenceCoverage;
        Items = new ReadOnlyCollection<ReadinessItemResult>(items.ToArray());
        WithholdingReasons = new ReadOnlyCollection<ReadinessScoreReason>(withholdingReasons.ToArray());
    }
}

public sealed class ReadinessScoringResult
{
    public string QuestionnaireVersion { get; }
    public ReadinessOverallStatus OverallStatus { get; }
    public double? OverallRawScore0To100 { get; }
    public int? OverallDisplayScore0To100 { get; }
    public ReadinessMaturityClassification? OverallClassification { get; }
    public ReadinessEvidenceProvenance EvidenceProvenance { get; }
    public IReadOnlyList<ReadinessDimensionResult> Dimensions { get; }
    public IReadOnlyList<ReadinessScoreReason> WithholdingReasons { get; }
    public IReadOnlyList<ReadinessReviewFlag> ReviewFlags { get; }

    internal ReadinessScoringResult(
        string questionnaireVersion,
        ReadinessOverallStatus overallStatus,
        double? overallRawScore0To100,
        int? overallDisplayScore0To100,
        ReadinessMaturityClassification? overallClassification,
        ReadinessEvidenceProvenance evidenceProvenance,
        IEnumerable<ReadinessDimensionResult> dimensions,
        IEnumerable<ReadinessScoreReason> withholdingReasons,
        IEnumerable<ReadinessReviewFlag> reviewFlags)
    {
        QuestionnaireVersion = questionnaireVersion;
        OverallStatus = overallStatus;
        OverallRawScore0To100 = overallRawScore0To100;
        OverallDisplayScore0To100 = overallDisplayScore0To100;
        OverallClassification = overallClassification;
        EvidenceProvenance = evidenceProvenance;
        Dimensions = new ReadOnlyCollection<ReadinessDimensionResult>(dimensions.ToArray());
        WithholdingReasons = new ReadOnlyCollection<ReadinessScoreReason>(withholdingReasons.ToArray());
        ReviewFlags = new ReadOnlyCollection<ReadinessReviewFlag>(reviewFlags.ToArray());
    }
}
