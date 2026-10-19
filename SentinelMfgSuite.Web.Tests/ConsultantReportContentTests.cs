using System.Text.Json;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class ConsultantReportContentTests
{
    [Fact]
    public void Serializer_RoundTrips_Schema_1_0_And_All_Six_Sections()
    {
        var content = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed, "Summary", Array.Empty<ReportFinding>(), Array.Empty<ReportEvidenceReference>()),
            IndustrialDataAndSystems = new IndustrialDataAndSystemsSection(ReportAssessmentState.NotApplicable),
            AiOpportunitiesAndRoi = new AiOpportunitiesAndRoiSection(ReportAssessmentState.NotAssessed),
            TechnicalGapsAndArchitecture = new TechnicalGapsAndArchitectureSection(ReportAssessmentState.NotAvailable),
            ImplementationRoadmap = new ImplementationRoadmapSection(ReportAssessmentState.NotApplicable),
            ExecutivePresentation = new ExecutivePresentationSection(ReportAssessmentState.Assessed, "Brief", "Summary", Array.Empty<ReportTextEntry>(), Array.Empty<ReportTextEntry>())
        };

        var json = ConsultantReportContentSerializer.Serialize(content);
        var roundTrip = ConsultantReportContentSerializer.Deserialize(json);

        Assert.Contains("\"schemaVersion\":\"1.0\"", json);
        Assert.Equal(json, ConsultantReportContentSerializer.Serialize(roundTrip));
        Assert.Contains("\"industrialDataAndSystems\"", json);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("")]
    public void Serializer_Rejects_Unsupported_Or_Missing_Version(string version)
    {
        var json = "{\"schemaVersion\":\"" + version + "\",\"executiveSummary\":{},\"industrialDataAndSystems\":{},\"aiOpportunitiesAndRoi\":{},\"technicalGapsAndArchitecture\":{},\"implementationRoadmap\":{},\"executivePresentation\":{}}";

        Assert.Throws<JsonException>(() => ConsultantReportContentSerializer.Deserialize(json));
    }

    [Fact]
    public void Validator_Draft_Allows_Incomplete_Content()
    {
        var result = new AssessmentReportContentValidator().Validate(
            ConsultantReportContent.CreateEmpty(), new(AssessmentReportContentStage.Draft));

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Validator_InReview_Requires_Sections_And_Valid_Evidence()
    {
        var content = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(
                ReportAssessmentState.Assessed,
                "Summary",
                new[] { new ReportFinding("finding-1", "Claim", "Claim", ReportFindingClassification.PreliminaryFinding,
                    ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified,
                    new[] { new ReportEvidenceReference(ReportEvidenceKind.ConsultantObservation, "missing") }) }),
            IndustrialDataAndSystems = new IndustrialDataAndSystemsSection(ReportAssessmentState.NotAvailable),
            AiOpportunitiesAndRoi = new AiOpportunitiesAndRoiSection(ReportAssessmentState.NotAvailable),
            TechnicalGapsAndArchitecture = new TechnicalGapsAndArchitectureSection(ReportAssessmentState.NotAvailable),
            ImplementationRoadmap = new ImplementationRoadmapSection(ReportAssessmentState.NotAvailable),
            ExecutivePresentation = new ExecutivePresentationSection(
                ReportAssessmentState.NotAvailable,
                EvidenceReferences: new[] { new ReportEvidenceReference(ReportEvidenceKind.ConsultantObservation, "valid-observation") })
        };

        var result = new AssessmentReportContentValidator().Validate(content,
            new(AssessmentReportContentStage.InReview, ConsultantObservationIds: new HashSet<string> { "valid-observation" }));

        Assert.Contains(result.Issues, issue => issue.Code == "evidence-reference-invalid");
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "section-not-assessed");
    }

    [Fact]
    public void Validator_Rejects_Scoring_Contradiction_And_Preserves_Null_NonEligible_Scores()
    {
        var snapshot = new ReadinessScoringSnapshot(
            "2.0", ReadinessOverallStatus.Withheld, null, null, null,
            ReadinessEvidenceProvenance.SelfReported, Array.Empty<ReadinessDimensionSnapshot>(),
            new[] { ReadinessScoreReason.OverallNotAvailableForV2 }, Array.Empty<ReadinessReviewFlagSnapshot>());
        var content = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(
                Readiness: new ReadinessScoreRepresentation(ReadinessOverallStatus.Withheld, 64.0, 64, ReadinessMaturityClassification.Established,
                    Array.Empty<ReadinessDimensionRepresentation>()))
        };

        var result = new AssessmentReportContentValidator().Validate(content,
            new(AssessmentReportContentStage.Draft, ScoringSnapshot: snapshot));

        Assert.Contains(result.Issues, issue => issue.Code == "readiness-score-conflict");
        Assert.Contains(result.Issues, issue => issue.Code == "score-present-for-noneligible-status");
        Assert.Null(snapshot.OverallRawScore0To100);
        Assert.Null(snapshot.OverallDisplayScore0To100);
        Assert.Equal(ReadinessOverallStatus.Withheld, snapshot.OverallStatus);
    }

    [Fact]
    public void Validator_Requires_Basis_For_Preliminary_Roi_And_NotEstimated_Cannot_Have_Values()
    {
        var opportunity = Opportunity() with
        {
            Estimate = new EffortCostEstimate(ReportEstimateStatus.Estimated, CostAmount: 1000, Currency: "USD")
        };
        var result = new AssessmentReportContentValidator().Validate(
            ConsultantReportContent.CreateEmpty() with
            {
                AiOpportunitiesAndRoi = new AiOpportunitiesAndRoiSection(ReportAssessmentState.Assessed, new[] { opportunity })
            }, new(AssessmentReportContentStage.Draft));
        var invalidNotEstimated = Opportunity() with
        {
            Estimate = new EffortCostEstimate(ReportEstimateStatus.NotEstimated, CostAmount: 10)
        };
        var invalidResult = new AssessmentReportContentValidator().Validate(
            ConsultantReportContent.CreateEmpty() with
            {
                AiOpportunitiesAndRoi = new AiOpportunitiesAndRoiSection(ReportAssessmentState.Assessed, new[] { invalidNotEstimated })
            }, new(AssessmentReportContentStage.Draft));

        Assert.Contains(result.Issues, issue => issue.Code == "estimate-basis-required");
        Assert.Contains(invalidResult.Issues, issue => issue.Code == "not-estimated-has-values");
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(61, 91)]
    [InlineData(40, 20)]
    public void Validator_Rejects_Roadmap_Items_Outside_90_Day_Bounds(int startDay, int endDay)
    {
        var roadmap = new RoadmapItem("step-1", "Pilot", "Pilot activities", startDay, endDay, 1, null,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null,
            ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified, Array.Empty<ReportEvidenceReference>());
        var result = new AssessmentReportContentValidator().Validate(
            ConsultantReportContent.CreateEmpty() with
            {
                ImplementationRoadmap = new ImplementationRoadmapSection(ReportAssessmentState.Assessed, new[] { roadmap })
            }, new(AssessmentReportContentStage.Draft));

        Assert.Contains(result.Issues, issue => issue.Code == "roadmap-boundary");
    }

    [Fact]
    public void Validator_Flags_Unsupported_Claims_And_Requires_Explicit_Preliminary_Classification()
    {
        var finding = new ReportFinding("finding-1", "ROI", "Guaranteed 300% return", ReportFindingClassification.UnsupportedClaim,
            ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified, Array.Empty<ReportEvidenceReference>());
        var result = new AssessmentReportContentValidator().Validate(
            ConsultantReportContent.CreateEmpty() with
            {
                ExecutiveSummary = new ExecutiveSummarySection(Findings: new[] { finding })
            }, new(AssessmentReportContentStage.Approved));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "unsupported-claim" && issue.Severity == ReportContentValidationSeverity.Error);
    }

    [Fact]
    public void Validator_Rejects_Content_In_NotAssessed_Section()
    {
        var result = new AssessmentReportContentValidator().Validate(
            ConsultantReportContent.CreateEmpty() with
            {
                ExecutiveSummary = new ExecutiveSummarySection(Narrative: "Unexpected content while not assessed")
            }, new(AssessmentReportContentStage.Draft));

        Assert.Contains(result.Issues, issue => issue.Code == "section-state-content-conflict");
    }

    private static AiImplementationOpportunity Opportunity() => new(
        "opportunity-1", "Reduce unplanned downtime", "Maintenance", "Predictive maintenance alerting", "Reduce downtime hours",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new[] { "Baseline not yet measured" },
        new EffortCostEstimate(ReportEstimateStatus.NotEstimated), new ImplementationScope(ReportAssessmentState.NotAssessed),
        ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified, Array.Empty<ReportEvidenceReference>());
}
