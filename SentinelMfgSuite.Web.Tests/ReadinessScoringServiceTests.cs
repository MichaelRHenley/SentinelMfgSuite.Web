using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class ReadinessScoringServiceTests
{
    private readonly ReadinessScoringService _service = new();

    public static TheoryData<string, string, string, int> Mappings => new()
    {
        { "3.0", "q15", "manual_transfers", 0 },
        { "3.0", "q15", "isolated", 1 },
        { "3.0", "q15", "partially", 2 },
        { "3.0", "q15", "mostly", 3 },
        { "3.0", "q33", "none", 0 },
        { "3.0", "q33", "less_than_quarter", 1 },
        { "3.0", "q33", "about_quarter_to_half", 2 },
        { "3.0", "q33", "most", 3 },
        { "3.0", "q33", "nearly_all", 4 },
        { "3.0", "q34", "not_available", 0 },
        { "3.0", "q34", "often_late_or_incomplete", 1 },
        { "3.0", "q34", "sometimes_late_or_incomplete", 2 },
        { "3.0", "q34", "usually_on_time", 3 },
        { "3.0", "q34", "consistently_reliable", 4 },
        { "2.0", "q17", "no_historical", 0 },
        { "2.0", "q17", "real_time_only", 1 },
        { "2.0", "q17", "some_sources", 2 },
        { "2.0", "q17", "most_relevant", 3 },
        { "2.0", "q19", "no", 0 },
        { "2.0", "q19", "partial", 2 },
        { "2.0", "q19", "yes", 4 },
        { "2.0", "q20", "none", 0 },
        { "2.0", "q20", "inconsistent", 1 },
        { "2.0", "q20", "partial", 2 },
        { "2.0", "q20", "standardized", 4 },
        { "2.0", "q21", "no", 0 },
        { "2.0", "q21", "manual_match", 1 },
        { "2.0", "q21", "partial", 2 },
        { "2.0", "q21", "automatic", 4 },
        { "3.0", "q35", "no_checks", 0 },
        { "3.0", "q35", "informal_checks", 1 },
        { "3.0", "q35", "some_checks", 2 },
        { "3.0", "q35", "routine_checks", 3 },
        { "3.0", "q35", "systematic_monitoring", 4 },
        { "3.0", "q36", "frequently_missing", 0 },
        { "3.0", "q36", "often_missing", 1 },
        { "3.0", "q36", "sometimes_missing", 2 },
        { "3.0", "q36", "rarely_missing", 3 },
        { "3.0", "q36", "almost_never_missing", 4 },
        { "2.0", "q24", "not_calculated", 0 },
        { "2.0", "q24", "manual", 1 },
        { "2.0", "q24", "mix", 2 },
        { "2.0", "q24", "automated_partial", 3 },
        { "2.0", "q24", "automated_validated", 4 },
        { "2.0", "q31", "frequent_discrepancies", 1 },
        { "2.0", "q31", "generally_accurate", 3 },
        { "2.0", "q31", "consistently_accurate", 4 },
        { "2.0", "q32", "not_used", 0 },
        { "2.0", "q32", "limited_ad_hoc", 1 },
        { "2.0", "q32", "retrospective_reporting", 2 },
        { "2.0", "q32", "investigate_improve", 3 },
        { "2.0", "q32", "proactive_decisions", 4 }
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Every_Mapped_Answer_Produces_Exact_Score(string version, string questionId, string value, int expectedScore)
    {
        var response = CreateScorableResponse(version);
        SetValue(response, questionId, value);

        var result = _service.Score(version, response);
        var item = result.Dimensions.SelectMany(dimension => dimension.Items).Single(item => item.QuestionId == questionId);

        Assert.Equal(ReadinessItemStatus.Scoreable, item.Status);
        Assert.Equal(expectedScore, item.Score0To4);
        Assert.Equal(value, item.RawValue);
    }

    [Theory]
    [InlineData("1.0", ReadinessOverallStatus.HistoricalNoScore, ReadinessScoreReason.HistoricalVersionNotScored)]
    [InlineData("4.0", ReadinessOverallStatus.UnsupportedQuestionnaireVersion, ReadinessScoreReason.UnsupportedQuestionnaireVersion)]
    public void Historical_And_Unsupported_Versions_Withhold_Scores(string version, ReadinessOverallStatus status, ReadinessScoreReason reason)
    {
        var result = _service.Score(version, new QuestionnaireResponseV1 { SchemaVersion = version });

        Assert.Equal(status, result.OverallStatus);
        Assert.Null(result.OverallRawScore0To100);
        Assert.Null(result.OverallClassification);
        Assert.Contains(reason, result.WithholdingReasons);
    }

    [Theory]
    [InlineData("2.0", "1.0")]
    [InlineData("3.0", "2.0")]
    public void Schema_Mismatch_Withholds_All_Dimensions(string version, string responseVersion)
    {
        var result = _service.Score(version, new QuestionnaireResponseV1 { SchemaVersion = responseVersion });

        Assert.Equal(ReadinessOverallStatus.Withheld, result.OverallStatus);
        Assert.Contains(ReadinessScoreReason.SchemaVersionMismatch, result.WithholdingReasons);
        Assert.Empty(result.Dimensions);
    }

    [Fact]
    public void V1_Schema_Mismatch_Is_Explicit_While_Remaining_Historical_No_Score()
    {
        var result = _service.Score("1.0", new QuestionnaireResponseV1 { SchemaVersion = "3.0" });

        Assert.Equal(ReadinessOverallStatus.HistoricalNoScore, result.OverallStatus);
        Assert.Contains(ReadinessScoreReason.SchemaVersionMismatch, result.WithholdingReasons);
        Assert.DoesNotContain(ReadinessScoreReason.HistoricalVersionNotScored, result.WithholdingReasons);
    }

    [Fact]
    public void Null_Response_Withholds_With_Schema_Mismatch_Reason()
    {
        var result = _service.Score("3.0", null);

        Assert.Contains(ReadinessScoreReason.SchemaVersionMismatch, result.WithholdingReasons);
        Assert.Empty(result.Dimensions);
    }

    [Fact]
    public void V2_Produces_Eligible_D2_D3_Only_And_Always_Withholds_Overall()
    {
        var response = CreateScorableResponse("2.0");

        var result = _service.Score("2.0", response);

        Assert.Equal(ReadinessOverallStatus.Withheld, result.OverallStatus);
        Assert.Null(result.OverallRawScore0To100);
        Assert.Contains(ReadinessScoreReason.OverallNotAvailableForV2, result.WithholdingReasons);
        Assert.Collection(result.Dimensions,
            d1 => { Assert.Equal("D1", d1.DimensionId); Assert.Equal(ReadinessDimensionStatus.Withheld, d1.Status); Assert.Contains(ReadinessScoreReason.NotScoredForV2, d1.WithholdingReasons); },
            d2 => { Assert.Equal("D2", d2.DimensionId); Assert.Equal(ReadinessDimensionStatus.Eligible, d2.Status); },
            d3 => { Assert.Equal("D3", d3.DimensionId); Assert.Equal(ReadinessDimensionStatus.Eligible, d3.Status); });
    }

    [Theory]
    [InlineData(2, ReadinessDimensionStatus.Withheld)]
    [InlineData(3, ReadinessDimensionStatus.Eligible)]
    public void V2_D2_Gate_Requires_Three_Of_Four(int scoreableItems, ReadinessDimensionStatus expected)
    {
        var response = CreateScorableResponse("2.0");
        if (scoreableItems == 2)
        {
            response.Answers.Remove("q20");
            response.Answers.Remove("q21");
        }
        else
        {
            response.Answers.Remove("q21");
        }

        var result = _service.Score("2.0", response).Dimensions.Single(dimension => dimension.DimensionId == "D2");

        Assert.Equal(expected, result.Status);
        Assert.Equal(scoreableItems, result.ScoreableItemCount);
        Assert.Equal((double)scoreableItems / 4, result.EvidenceCoverage);
    }

    [Theory]
    [InlineData(1, ReadinessDimensionStatus.Withheld)]
    [InlineData(2, ReadinessDimensionStatus.Eligible)]
    public void V2_D3_Gate_Requires_Two_Of_Three(int scoreableItems, ReadinessDimensionStatus expected)
    {
        var response = CreateScorableResponse("2.0");
        if (scoreableItems == 2)
        {
            response.Answers.Remove("q32");
        }
        else
        {
            response.Answers.Remove("q31");
            response.Answers.Remove("q32");
        }

        var result = _service.Score("2.0", response).Dimensions.Single(dimension => dimension.DimensionId == "D3");

        Assert.Equal(expected, result.Status);
        Assert.Equal(scoreableItems, result.ScoreableItemCount);
    }

    [Fact]
    public void V2_D1_Is_Always_Withheld_Without_Evaluated_Items()
    {
        var response = CreateScorableResponse("2.0");

        var result = _service.Score("2.0", response).Dimensions.Single(dimension => dimension.DimensionId == "D1");

        Assert.Equal(ReadinessDimensionStatus.Withheld, result.Status);
        Assert.Equal(0, result.TotalEligibleItemCount);
        Assert.Null(result.EvidenceCoverage);
    }

    [Theory]
    [InlineData("D1", new[] { "q15" }, 1, false)]
    [InlineData("D1", new[] { "q15", "q33" }, 2, true)]
    [InlineData("D2", new[] { "q19", "q20", "q21", "q35" }, 4, true)]
    [InlineData("D2", new[] { "q19", "q20", "q21", "q17" }, 4, false)]
    public void V3_Dimension_Gates_Require_Count_And_New_Evidence(string dimensionId, string[] presentIds, int count, bool eligible)
    {
        var response = CreateEmptyResponse("3.0");
        foreach (var id in presentIds)
        {
            SetValue(response, id, DefaultValue(id));
        }

        var result = _service.Score("3.0", response).Dimensions.Single(dimension => dimension.DimensionId == dimensionId);

        Assert.Equal(eligible ? ReadinessDimensionStatus.Eligible : ReadinessDimensionStatus.Withheld, result.Status);
        Assert.Equal(count, result.ScoreableItemCount);
        Assert.Equal(eligible, result.RawScore0To100.HasValue);
    }

    [Theory]
    [InlineData("q33", ReadinessItemStatus.Missing)]
    [InlineData("q33", ReadinessItemStatus.Unknown, "unknown")]
    [InlineData("q31", ReadinessItemStatus.ReliabilityNotEstablished, "reliability_not_established")]
    [InlineData("q34", ReadinessItemStatus.UnmappedValue, "invented_value")]
    public void Insufficient_And_Unmapped_Answers_Remain_Distinct(string questionId, ReadinessItemStatus expected, string? value = null)
    {
        var response = CreateScorableResponse("3.0");
        if (value is not null)
        {
            SetValue(response, questionId, value);
        }
        else
        {
            response.Answers.Remove(questionId);
        }

        var item = _service.Score("3.0", response).Dimensions.SelectMany(dimension => dimension.Items).Single(item => item.QuestionId == questionId);

        Assert.Equal(expected, item.Status);
        Assert.Null(item.Score0To4);
    }

    [Fact]
    public void Composite_Items_Use_Only_Named_Selected_Field()
    {
        var response = CreateScorableResponse("3.0");
        response.Answers["q15"] = new QuestionAnswer
        {
            Composite = new Dictionary<string, FieldAnswer>
            {
                ["integration_level"] = new() { Selected = "mostly" },
                ["integration_notes"] = new() { Text = "Not part of scoring" }
            }
        };
        response.Answers["q17"] = new QuestionAnswer
        {
            Composite = new Dictionary<string, FieldAnswer>
            {
                ["history_choice"] = new() { Selected = "some_sources" },
                ["retention"] = new() { Selected = ">12m" }
            }
        };

        var result = _service.Score("3.0", response);
        var q15 = result.Dimensions.Single(dimension => dimension.DimensionId == "D1").Items.Single(item => item.QuestionId == "q15");
        var q17 = result.Dimensions.Single(dimension => dimension.DimensionId == "D2").Items.Single(item => item.QuestionId == "q17");

        Assert.Equal("q15.integration_level", q15.ItemKey);
        Assert.Equal(3, q15.Score0To4);
        Assert.Equal("q17.history_choice", q17.ItemKey);
        Assert.Equal(2, q17.Score0To4);
    }

    [Fact]
    public void Invalid_Composite_Shape_Is_Not_Coerced()
    {
        var response = CreateScorableResponse("3.0");
        response.Answers["q15"] = new QuestionAnswer { Selected = "mostly" };

        var item = _service.Score("3.0", response).Dimensions.Single(dimension => dimension.DimensionId == "D1").Items.Single(item => item.QuestionId == "q15");

        Assert.Equal(ReadinessItemStatus.InvalidAnswerShape, item.Status);
        Assert.Null(item.Score0To4);
    }

    [Fact]
    public void Missing_Unknown_And_Unmapped_Items_Remain_In_Coverage_Denominator()
    {
        var response = CreateEmptyResponse("3.0");
        SetValue(response, "q15", "unknown");
        SetValue(response, "q33", "invented");
        SetValue(response, "q34", "consistently_reliable");

        var d1 = _service.Score("3.0", response).Dimensions.Single(dimension => dimension.DimensionId == "D1");

        Assert.Equal(1, d1.ScoreableItemCount);
        Assert.Equal(3, d1.TotalEligibleItemCount);
        Assert.Equal(1d / 3d, d1.EvidenceCoverage);
        Assert.Equal(ReadinessDimensionStatus.Withheld, d1.Status);
    }

    [Fact]
    public void V3_Overall_Uses_Unrounded_Dimension_Scores_And_All_Dimension_Gates()
    {
        var response = CreateEmptyResponse("3.0");
        SetValue(response, "q15", "mostly");
        SetValue(response, "q33", "nearly_all");
        SetValue(response, "q35", "systematic_monitoring");
        SetValue(response, "q36", "almost_never_missing");
        SetValue(response, "q19", "partial");
        SetValue(response, "q20", "standardized");
        SetValue(response, "q24", "manual");
        SetValue(response, "q31", "generally_accurate");
        SetValue(response, "q32", "proactive_decisions");

        var result = _service.Score("3.0", response);

        Assert.Equal(ReadinessOverallStatus.Eligible, result.OverallStatus);
        Assert.Equal(87.5, result.Dimensions.Single(dimension => dimension.DimensionId == "D1").RawScore0To100);
        Assert.Equal(87.5, result.Dimensions.Single(dimension => dimension.DimensionId == "D2").RawScore0To100!.Value, 10);
        Assert.Equal(66.66666666666667, result.Dimensions.Single(dimension => dimension.DimensionId == "D3").RawScore0To100!.Value, 10);
        Assert.Equal(82.29166666666667, result.OverallRawScore0To100!.Value, 10);
        Assert.Equal(82, result.OverallDisplayScore0To100);
        Assert.Equal(ReadinessMaturityClassification.Advanced, result.OverallClassification);
    }

    [Fact]
    public void V3_Overall_Is_Withheld_When_Any_Dimension_Fails()
    {
        var response = CreateScorableResponse("3.0");
        response.Answers.Remove("q33");
        response.Answers.Remove("q34");

        var result = _service.Score("3.0", response);

        Assert.Equal(ReadinessOverallStatus.Withheld, result.OverallStatus);
        Assert.Null(result.OverallRawScore0To100);
        Assert.Contains(ReadinessScoreReason.OverallRequiresAllDimensions, result.WithholdingReasons);
    }

    [Theory]
    [InlineData(24.49, 24, ReadinessMaturityClassification.Foundational)]
    [InlineData(24.5, 25, ReadinessMaturityClassification.Developing)]
    [InlineData(49.5, 50, ReadinessMaturityClassification.Established)]
    [InlineData(74.49, 74, ReadinessMaturityClassification.Established)]
    [InlineData(74.5, 75, ReadinessMaturityClassification.Advanced)]
    public void Display_Rounding_And_Classification_Follow_Contract(double raw, int display, ReadinessMaturityClassification classification)
    {
        Assert.Equal(display, ReadinessScoringService.RoundForDisplay(raw));
        Assert.Equal(classification, ReadinessScoringService.Classify(display));
    }

    [Theory]
    [InlineData("q33", "none", "q34", "usually_on_time", "ConnectivityReliabilityConflict")]
    [InlineData("q33", "none", "q34", "consistently_reliable", "ConnectivityReliabilityConflict")]
    [InlineData("q35", "systematic_monitoring", "q36", "frequently_missing", "DataCompletenessReview")]
    [InlineData("q35", "systematic_monitoring", "q36", "often_missing", "DataCompletenessReview")]
    [InlineData("q24", "not_calculated", "q31", "consistently_accurate", "ReportingReliabilityConflict")]
    public void Contradiction_Flags_Are_Advisory_And_Do_Not_Change_Eligibility(string id1, string value1, string id2, string value2, string flagCode)
    {
        var response = CreateScorableResponse("3.0");
        SetValue(response, id1, value1);
        SetValue(response, id2, value2);

        var result = _service.Score("3.0", response);

        Assert.Contains(result.ReviewFlags, flag => flag.Code == flagCode);
        Assert.All(result.ReviewFlags, flag => Assert.Equal("Informational", flag.Severity));
        Assert.Equal(ReadinessDimensionStatus.Eligible, result.Dimensions.Single(dimension => dimension.DimensionId == "D1").Status);
        Assert.Equal(ReadinessDimensionStatus.Eligible, result.Dimensions.Single(dimension => dimension.DimensionId == "D2").Status);
        Assert.Equal(ReadinessDimensionStatus.Eligible, result.Dimensions.Single(dimension => dimension.DimensionId == "D3").Status);
    }

    [Fact]
    public void Result_Is_Deterministic_And_Does_Not_Mutate_Response()
    {
        var response = CreateScorableResponse("3.0");
        var before = QuestionnaireResponseV1Serializer.Serialize(response);

        var first = _service.Score("3.0", response);
        var second = _service.Score("3.0", response);

        Assert.Equal(before, QuestionnaireResponseV1Serializer.Serialize(response));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(ReadinessEvidenceProvenance.SelfReported, first.EvidenceProvenance);
    }

    [Fact]
    public void Item_Contributions_Are_Transparent_And_Sum_To_Dimension_Score()
    {
        var response = CreateEmptyResponse("3.0");
        SetValue(response, "q15", "mostly");
        SetValue(response, "q33", "nearly_all");
        SetValue(response, "q34", "unknown");

        var d1 = _service.Score("3.0", response).Dimensions.Single(dimension => dimension.DimensionId == "D1");

        Assert.Equal(2, d1.ScoreableItemCount);
        Assert.Equal(3, d1.TotalEligibleItemCount);
        Assert.Equal(87.5, d1.RawScore0To100);
        Assert.Equal(37.5, d1.Items.Single(item => item.QuestionId == "q15").DimensionContribution);
        Assert.Equal(50, d1.Items.Single(item => item.QuestionId == "q33").DimensionContribution);
        Assert.Null(d1.Items.Single(item => item.QuestionId == "q34").DimensionContribution);
        Assert.Equal(d1.RawScore0To100, d1.Items.Sum(item => item.DimensionContribution ?? 0));
    }

    [Fact]
    public void Q13_Q14_And_Q17_Retention_Do_Not_Contribute()
    {
        var response = CreateScorableResponse("3.0");
        SetValue(response, "q13", "plc");
        SetValue(response, "q14", "auto_equipment");
        response.Answers["q17"] = new QuestionAnswer
        {
            Composite = new Dictionary<string, FieldAnswer> { ["retention"] = new() { Selected = ">12m" } }
        };

        var result = _service.Score("3.0", response);
        var d1 = result.Dimensions.Single(dimension => dimension.DimensionId == "D1");
        var d2 = result.Dimensions.Single(dimension => dimension.DimensionId == "D2");

        Assert.DoesNotContain(d1.Items, item => item.QuestionId is "q13" or "q14");
        Assert.DoesNotContain(d2.Items, item => item.ItemKey == "q17.retention");
        Assert.Equal(ReadinessItemStatus.Missing, d2.Items.Single(item => item.QuestionId == "q17").Status);
    }

    private static QuestionnaireResponseV1 CreateScorableResponse(string version)
    {
        var response = CreateEmptyResponse(version);
        foreach (var dimension in version == "2.0" ? new[] { "q17", "q19", "q20", "q21", "q24", "q31", "q32" } : new[] { "q15", "q17", "q19", "q20", "q21", "q24", "q31", "q32", "q33", "q34", "q35", "q36" })
        {
            SetValue(response, dimension, DefaultValue(dimension));
        }

        return response;
    }

    private static QuestionnaireResponseV1 CreateEmptyResponse(string version)
        => new() { SchemaVersion = version };

    private static string DefaultValue(string questionId)
        => questionId switch
        {
            "q15" => "mostly",
            "q17" => "most_relevant",
            "q19" => "yes",
            "q20" => "standardized",
            "q21" => "automatic",
            "q24" => "automated_validated",
            "q31" => "consistently_accurate",
            "q32" => "proactive_decisions",
            "q33" => "nearly_all",
            "q34" => "consistently_reliable",
            "q35" => "systematic_monitoring",
            "q36" => "almost_never_missing",
            _ => throw new ArgumentOutOfRangeException(nameof(questionId), questionId, null)
        };

    private static void SetValue(QuestionnaireResponseV1 response, string questionId, string value)
    {
        if (questionId is "q15" or "q17")
        {
            var fieldId = questionId == "q15" ? "integration_level" : "history_choice";
            if (!response.Answers.TryGetValue(questionId, out var composite) || composite.Composite is null)
            {
                composite = new QuestionAnswer { Composite = new Dictionary<string, FieldAnswer>() };
                response.Answers[questionId] = composite;
            }

            composite.Composite[fieldId] = new FieldAnswer { Selected = value };
            return;
        }

        response.Answers[questionId] = new QuestionAnswer { Selected = value };
    }
}
