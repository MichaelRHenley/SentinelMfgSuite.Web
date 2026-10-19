using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

namespace SentinelMfgSuite.Web.Services.Assessments.Scoring;

public sealed class ReadinessScoringService : IReadinessScoringService
{
    private static readonly DimensionDefinition[] V2Dimensions =
    {
        new("D1", "Connectivity & Integration", 0.30m, Array.Empty<ItemDefinition>(), 0, null, ReadinessScoreReason.NotScoredForV2),
        new("D2", "Data Quality & Contextualization", 0.45m, new[]
        {
            Composite("q17", "history_choice", "no_historical", 0, "real_time_only", 1, "some_sources", 2, "most_relevant", 3),
            Question("q19", "no", 0, "partial", 2, "yes", 4),
            Question("q20", "none", 0, "inconsistent", 1, "partial", 2, "standardized", 4),
            Question("q21", "no", 0, "manual_match", 1, "partial", 2, "automatic", 4)
        }, 3),
        new("D3", "Analytics & Reporting", 0.25m, new[]
        {
            Question("q24", "not_calculated", 0, "manual", 1, "mix", 2, "automated_partial", 3, "automated_validated", 4),
            Question("q31", "frequent_discrepancies", 1, "generally_accurate", 3, "consistently_accurate", 4),
            Question("q32", "not_used", 0, "limited_ad_hoc", 1, "retrospective_reporting", 2, "investigate_improve", 3, "proactive_decisions", 4)
        }, 2)
    };

    private static readonly DimensionDefinition[] V3Dimensions =
    {
        new("D1", "Connectivity & Integration", 0.30m, new[]
        {
            Composite("q15", "integration_level", "manual_transfers", 0, "isolated", 1, "partially", 2, "mostly", 3),
            Question("q33", "none", 0, "less_than_quarter", 1, "about_quarter_to_half", 2, "most", 3, "nearly_all", 4),
            Question("q34", "not_available", 0, "often_late_or_incomplete", 1, "sometimes_late_or_incomplete", 2, "usually_on_time", 3, "consistently_reliable", 4)
        }, 2, new[] { "q33", "q34" }),
        new("D2", "Data Quality & Contextualization", 0.45m, new[]
        {
            Composite("q17", "history_choice", "no_historical", 0, "real_time_only", 1, "some_sources", 2, "most_relevant", 3),
            Question("q19", "no", 0, "partial", 2, "yes", 4),
            Question("q20", "none", 0, "inconsistent", 1, "partial", 2, "standardized", 4),
            Question("q21", "no", 0, "manual_match", 1, "partial", 2, "automatic", 4),
            Question("q35", "no_checks", 0, "informal_checks", 1, "some_checks", 2, "routine_checks", 3, "systematic_monitoring", 4),
            Question("q36", "frequently_missing", 0, "often_missing", 1, "sometimes_missing", 2, "rarely_missing", 3, "almost_never_missing", 4)
        }, 4, new[] { "q35", "q36" }),
        new("D3", "Analytics & Reporting", 0.25m, new[]
        {
            Question("q24", "not_calculated", 0, "manual", 1, "mix", 2, "automated_partial", 3, "automated_validated", 4),
            Question("q31", "frequent_discrepancies", 1, "generally_accurate", 3, "consistently_accurate", 4),
            Question("q32", "not_used", 0, "limited_ad_hoc", 1, "retrospective_reporting", 2, "investigate_improve", 3, "proactive_decisions", 4)
        }, 2)
    };

    public ReadinessScoringResult Score(string? questionnaireVersion, QuestionnaireResponseV1? response)
    {
        var version = questionnaireVersion ?? string.Empty;
        if (version == "1.0")
        {
            var reason = response is null || !string.Equals(response.SchemaVersion, version, StringComparison.Ordinal)
                ? ReadinessScoreReason.SchemaVersionMismatch
                : ReadinessScoreReason.HistoricalVersionNotScored;
            return Result(version, ReadinessOverallStatus.HistoricalNoScore, null, null, null,
                Array.Empty<ReadinessDimensionResult>(), new[] { reason }, Array.Empty<ReadinessReviewFlag>());
        }

        if (version is not ("2.0" or "3.0"))
        {
            return Result(version, ReadinessOverallStatus.UnsupportedQuestionnaireVersion, null, null, null,
                Array.Empty<ReadinessDimensionResult>(), new[] { ReadinessScoreReason.UnsupportedQuestionnaireVersion }, Array.Empty<ReadinessReviewFlag>());
        }

        if (response is null || !string.Equals(response.SchemaVersion, version, StringComparison.Ordinal))
        {
            return Result(version, ReadinessOverallStatus.Withheld, null, null, null,
                Array.Empty<ReadinessDimensionResult>(), new[] { ReadinessScoreReason.SchemaVersionMismatch }, Array.Empty<ReadinessReviewFlag>());
        }

        var dimensionDefinitions = version == "2.0" ? V2Dimensions : V3Dimensions;
        var dimensions = dimensionDefinitions.Select(definition => ScoreDimension(version, response, definition)).ToArray();
        var flags = FindReviewFlags(version, response);

        if (version == "2.0")
        {
            return Result(version, ReadinessOverallStatus.Withheld, null, null, null, dimensions,
                new[] { ReadinessScoreReason.OverallNotAvailableForV2 }, flags);
        }

        if (dimensions.Any(dimension => dimension.Status != ReadinessDimensionStatus.Eligible))
        {
            return Result(version, ReadinessOverallStatus.Withheld, null, null, null, dimensions,
                new[] { ReadinessScoreReason.OverallRequiresAllDimensions }, flags);
        }

        var overallRaw = dimensions.Sum(dimension => (double)dimension.Weight * dimension.RawScore0To100!.Value);
        var overallDisplay = RoundForDisplay(overallRaw);
        return Result(version, ReadinessOverallStatus.Eligible, overallRaw, overallDisplay, Classify(overallDisplay), dimensions,
            Array.Empty<ReadinessScoreReason>(), flags);
    }

    private static ReadinessDimensionResult ScoreDimension(string version, QuestionnaireResponseV1 response, DimensionDefinition definition)
    {
        var items = definition.Items.Select(item => ScoreItem(response, item)).ToArray();
        var scoreable = items.Count(item => item.Status == ReadinessItemStatus.Scoreable);
        double? coverage = definition.Items.Length == 0 ? null : (double)scoreable / definition.Items.Length;
        var reasons = new List<ReadinessScoreReason>();

        if (definition.AlwaysWithheldReason is { } alwaysWithheldReason)
        {
            reasons.Add(alwaysWithheldReason);
        }
        else
        {
            if (scoreable < definition.MinimumScoreableItems)
            {
                reasons.Add(ReadinessScoreReason.InsufficientScoreableItems);
            }

            if (definition.RequiredEvidenceQuestionIds is { Length: > 0 } requiredIds
                && !items.Any(item => requiredIds.Contains(item.QuestionId, StringComparer.Ordinal)
                    && item.Status == ReadinessItemStatus.Scoreable))
            {
                reasons.Add(ReadinessScoreReason.RequiredEvidenceGroupMissing);
            }
        }

        var eligible = reasons.Count == 0;
        double? rawScore = null;
        int? displayScore = null;
        ReadinessMaturityClassification? classification = null;

        if (eligible)
        {
            rawScore = items.Where(item => item.Score0To4.HasValue).Average(item => item.Score0To4!.Value) * 25d;
            displayScore = RoundForDisplay(rawScore.Value);
            classification = Classify(displayScore.Value);
        }

        var completedItems = items.Select(item =>
        {
            var normalized = item.Score0To4.HasValue ? item.Score0To4.Value * 25d : (double?)null;
            var contribution = eligible && item.Score0To4.HasValue
                ? item.Score0To4.Value * 25d / scoreable
                : (double?)null;
            return item with { NormalizedScore0To100 = normalized, DimensionContribution = contribution };
        }).ToArray();

        return new ReadinessDimensionResult(
            definition.Id,
            definition.Name,
            definition.Weight,
            eligible ? ReadinessDimensionStatus.Eligible : ReadinessDimensionStatus.Withheld,
            rawScore,
            displayScore,
            classification,
            scoreable,
            definition.Items.Length,
            coverage,
            completedItems,
            reasons);
    }

    private static ReadinessItemResult ScoreItem(QuestionnaireResponseV1 response, ItemDefinition definition)
    {
        if (!response.Answers.TryGetValue(definition.QuestionId, out var answer))
        {
            return Item(definition, null, ReadinessItemStatus.Missing, null, "Missing");
        }

        if (answer is null)
        {
            return Item(definition, null, ReadinessItemStatus.InvalidAnswerShape, null, "AnswerPayloadNull");
        }

        string? value;
        if (definition.FieldId is not null)
        {
            if (answer.Text is not null || answer.Selected is not null || answer.SelectedValues is not null
                || answer.Supplemental is not null || answer.Repeatable is not null)
            {
                return Item(definition, null, ReadinessItemStatus.InvalidAnswerShape, null, "ExpectedCompositeAnswer");
            }

            if (answer.Composite is null)
            {
                return Item(definition, null, ReadinessItemStatus.Missing, null, "MissingCompositeAnswer");
            }

            if (!answer.Composite.TryGetValue(definition.FieldId, out var fieldAnswer))
            {
                return Item(definition, null, ReadinessItemStatus.Missing, null, "MissingCompositeField");
            }

            if (fieldAnswer is null || fieldAnswer.Text is not null || fieldAnswer.SelectedValues is not null)
            {
                return Item(definition, null, ReadinessItemStatus.InvalidAnswerShape, null, "ExpectedSingleSelectCompositeField");
            }

            value = fieldAnswer.Selected;
        }
        else
        {
            if (answer.Text is not null || answer.SelectedValues is not null || answer.Supplemental is not null
                || answer.Composite is not null || answer.Repeatable is not null)
            {
                return Item(definition, null, ReadinessItemStatus.InvalidAnswerShape, null, "ExpectedSingleSelectAnswer");
            }

            value = answer.Selected;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return Item(definition, value, ReadinessItemStatus.Missing, null, "MissingSelectedValue");
        }

        if (string.Equals(value, "unknown", StringComparison.Ordinal))
        {
            return Item(definition, value, ReadinessItemStatus.Unknown, null, "Unknown");
        }

        if (definition.QuestionId == "q31" && string.Equals(value, "reliability_not_established", StringComparison.Ordinal))
        {
            return Item(definition, value, ReadinessItemStatus.ReliabilityNotEstablished, null, "ReliabilityNotEstablished");
        }

        if (!definition.Scores.TryGetValue(value, out var score))
        {
            return Item(definition, value, ReadinessItemStatus.UnmappedValue, null, "UnmappedValue");
        }

        return Item(definition, value, ReadinessItemStatus.Scoreable, score, null);
    }

    private static ReadinessItemResult Item(ItemDefinition definition, string? value, ReadinessItemStatus status, int? score, string? reason)
        => new(definition.QuestionId, definition.FieldId, definition.Key, value, status, score,
            score.HasValue ? score.Value * 25d : null, null, reason);

    private static IReadOnlyList<ReadinessReviewFlag> FindReviewFlags(string version, QuestionnaireResponseV1 response)
    {
        var flags = new List<ReadinessReviewFlag>();
        if (version == "3.0"
            && GetSelected(response, "q33") == "none"
            && GetSelected(response, "q34") is { } q34 && q34 is "usually_on_time" or "consistently_reliable")
        {
            flags.Add(ReadinessReviewFlag.Create("ConnectivityReliabilityConflict",
                "q33 reports no needed data available through automatic connections, while q34 reports reliable timing for automatic exchange. Clarify the scope of connected systems; limited connections may still be reliable.",
                ("q33", "none"), ("q34", q34)));
        }

        if (version == "3.0"
            && GetSelected(response, "q35") == "systematic_monitoring"
            && GetSelected(response, "q36") is { } q36 && q36 is "frequently_missing" or "often_missing")
        {
            flags.Add(ReadinessReviewFlag.Create("DataCompletenessReview",
                "q35 reports systematic data-quality monitoring while q36 reports frequent missing records. Clarify detection versus remediation.",
                ("q35", "systematic_monitoring"), ("q36", q36)));
        }

        if (GetSelected(response, "q24") == "not_calculated" && GetSelected(response, "q31") == "consistently_accurate")
        {
            flags.Add(ReadinessReviewFlag.Create("ReportingReliabilityConflict",
                "q24 reports KPIs are not calculated while q31 reports consistently accurate operational reports. Clarify report/KPI scope and time period.",
                ("q24", "not_calculated"), ("q31", "consistently_accurate")));
        }

        return flags.AsReadOnly();
    }

    private static string? GetSelected(QuestionnaireResponseV1 response, string questionId)
        => response.Answers.TryGetValue(questionId, out var answer) ? answer?.Selected : null;

    private static ReadinessScoringResult Result(
        string version,
        ReadinessOverallStatus status,
        double? rawScore,
        int? displayScore,
        ReadinessMaturityClassification? classification,
        IEnumerable<ReadinessDimensionResult> dimensions,
        IEnumerable<ReadinessScoreReason> reasons,
        IEnumerable<ReadinessReviewFlag> flags)
        => new(version, status, rawScore, displayScore, classification, ReadinessEvidenceProvenance.SelfReported,
            dimensions, reasons, flags);

    internal static int RoundForDisplay(double value)
        => checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    internal static ReadinessMaturityClassification Classify(int displayScore)
        => displayScore switch
        {
            <= 24 => ReadinessMaturityClassification.Foundational,
            <= 49 => ReadinessMaturityClassification.Developing,
            <= 74 => ReadinessMaturityClassification.Established,
            _ => ReadinessMaturityClassification.Advanced
        };

    private static ItemDefinition Question(string questionId, params object[] mapping)
        => new(questionId, null, BuildKey(questionId, null), BuildScores(mapping));

    private static ItemDefinition Composite(string questionId, string fieldId, params object[] mapping)
        => new(questionId, fieldId, BuildKey(questionId, fieldId), BuildScores(mapping));

    private static string BuildKey(string questionId, string? fieldId)
        => fieldId is null ? questionId : $"{questionId}.{fieldId}";

    private static IReadOnlyDictionary<string, int> BuildScores(object[] mapping)
    {
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < mapping.Length; index += 2)
        {
            scores.Add((string)mapping[index], (int)mapping[index + 1]);
        }

        return scores;
    }

    private sealed record ItemDefinition(
        string QuestionId,
        string? FieldId,
        string Key,
        IReadOnlyDictionary<string, int> Scores);

    private sealed record DimensionDefinition(
        string Id,
        string Name,
        decimal Weight,
        ItemDefinition[] Items,
        int MinimumScoreableItems,
        string[]? RequiredEvidenceQuestionIds = null,
        ReadinessScoreReason? AlwaysWithheldReason = null);
}
