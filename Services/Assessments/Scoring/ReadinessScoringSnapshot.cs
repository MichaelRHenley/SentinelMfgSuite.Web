using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelMfgSuite.Web.Services.Assessments.Scoring;

public sealed record ReadinessScoringSnapshot(
    string QuestionnaireVersion,
    ReadinessOverallStatus OverallStatus,
    double? OverallRawScore0To100,
    int? OverallDisplayScore0To100,
    ReadinessMaturityClassification? OverallClassification,
    ReadinessEvidenceProvenance EvidenceProvenance,
    IReadOnlyList<ReadinessDimensionSnapshot> Dimensions,
    IReadOnlyList<ReadinessScoreReason> WithholdingReasons,
    IReadOnlyList<ReadinessReviewFlagSnapshot> ReviewFlags);

public sealed record ReadinessDimensionSnapshot(
    string DimensionId,
    string Name,
    decimal Weight,
    ReadinessDimensionStatus Status,
    double? RawScore0To100,
    int? DisplayScore0To100,
    ReadinessMaturityClassification? Classification,
    int ScoreableItemCount,
    int TotalEligibleItemCount,
    double? EvidenceCoverage,
    IReadOnlyList<ReadinessItemSnapshot> Items,
    IReadOnlyList<ReadinessScoreReason> WithholdingReasons);

public sealed record ReadinessItemSnapshot(
    string QuestionId,
    string? FieldId,
    string ItemKey,
    string? RawValue,
    ReadinessItemStatus Status,
    int? Score0To4,
    double? NormalizedScore0To100,
    double? DimensionContribution,
    string? Reason);

public sealed record ReadinessReviewFlagSnapshot(
    string Code,
    string Severity,
    string Message,
    IReadOnlyList<string> QuestionIds,
    IReadOnlyList<string> AnswerValues);

public sealed record ReadinessScoringSnapshotEnvelope(
    string SchemaVersion,
    ReadinessScoringSnapshot Snapshot);

public static class ReadinessScoringSnapshotSerializer
{
    public const string CurrentSchemaVersion = "1.0";
    public const int PersistedSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(ReadinessScoringResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var snapshot = new ReadinessScoringSnapshot(
            result.QuestionnaireVersion,
            result.OverallStatus,
            result.OverallRawScore0To100,
            result.OverallDisplayScore0To100,
            result.OverallClassification,
            result.EvidenceProvenance,
            result.Dimensions.Select(dimension => new ReadinessDimensionSnapshot(
                dimension.DimensionId,
                dimension.Name,
                dimension.Weight,
                dimension.Status,
                dimension.RawScore0To100,
                dimension.DisplayScore0To100,
                dimension.Classification,
                dimension.ScoreableItemCount,
                dimension.TotalEligibleItemCount,
                dimension.EvidenceCoverage,
                dimension.Items.Select(item => new ReadinessItemSnapshot(
                    item.QuestionId,
                    item.FieldId,
                    item.ItemKey,
                    item.RawValue,
                    item.Status,
                    item.Score0To4,
                    item.NormalizedScore0To100,
                    item.DimensionContribution,
                    item.Reason)).ToArray(),
                dimension.WithholdingReasons.ToArray())).ToArray(),
            result.WithholdingReasons.ToArray(),
            result.ReviewFlags.Select(flag => new ReadinessReviewFlagSnapshot(
                flag.Code,
                flag.Severity,
                flag.Message,
                flag.QuestionIds.ToArray(),
                flag.AnswerValues.ToArray())).ToArray());

        return JsonSerializer.Serialize(new ReadinessScoringSnapshotEnvelope(CurrentSchemaVersion, snapshot), Options);
    }

    public static ReadinessScoringSnapshotEnvelope Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var envelope = JsonSerializer.Deserialize<ReadinessScoringSnapshotEnvelope>(json, Options)
            ?? throw new JsonException("Scoring snapshot content is empty.");
        if (!string.Equals(envelope.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported scoring snapshot schema version '{envelope.SchemaVersion}'.");
        }

        return envelope;
    }
}