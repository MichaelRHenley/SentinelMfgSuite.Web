using System.Text.Json;
using System.Text.Json.Serialization;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Models.Assessments.Reports;

public sealed record ConsultantReportContent(
    string SchemaVersion,
    ExecutiveSummarySection ExecutiveSummary,
    IndustrialDataAndSystemsSection IndustrialDataAndSystems,
    AiOpportunitiesAndRoiSection AiOpportunitiesAndRoi,
    TechnicalGapsAndArchitectureSection TechnicalGapsAndArchitecture,
    ImplementationRoadmapSection ImplementationRoadmap,
    ExecutivePresentationSection ExecutivePresentation)
{
    public const string CurrentSchemaVersion = "1.0";

    public static ConsultantReportContent CreateEmpty() => new(
        CurrentSchemaVersion,
        new ExecutiveSummarySection(),
        new IndustrialDataAndSystemsSection(),
        new AiOpportunitiesAndRoiSection(),
        new TechnicalGapsAndArchitectureSection(),
        new ImplementationRoadmapSection(),
        new ExecutivePresentationSection());
}

public enum ReportContentProvenance
{
    ConsultantEntered,
    QuestionnaireDerived,
    ScoringDerived
}

public enum ReportVerificationStatus
{
    Unverified,
    SelfReported,
    ConsultantVerified,
    NotApplicable
}

public enum ReportAssessmentState
{
    Assessed,
    NotAssessed,
    NotAvailable,
    NotApplicable
}

public enum ReportEstimateStatus
{
    NotEstimated,
    Estimated
}

public enum ReportFindingClassification
{
    Finding,
    PreliminaryFinding,
    UnsupportedClaim
}

public enum ReportEvidenceKind
{
    QuestionnaireAnswer,
    ScoringItem,
    ScoringDimension,
    ScoringReviewFlag,
    ConsultantObservation
}

public sealed record ReportEvidenceReference(
    ReportEvidenceKind Kind,
    string ReferenceId,
    string? QuestionId = null,
    string? FieldId = null,
    string? ItemKey = null);

public sealed record ReportTextEntry(
    string Id,
    string Text,
    ReportContentProvenance Provenance,
    ReportVerificationStatus VerificationStatus,
    IReadOnlyList<ReportEvidenceReference> EvidenceReferences);

public sealed record ReportFinding(
    string Id,
    string Title,
    string Narrative,
    ReportFindingClassification Classification,
    ReportContentProvenance Provenance,
    ReportVerificationStatus VerificationStatus,
    IReadOnlyList<ReportEvidenceReference> EvidenceReferences,
    string? ConsultantNotes = null);

public sealed record ExecutiveSummarySection(
    ReportAssessmentState State = ReportAssessmentState.NotAssessed,
    string? Narrative = null,
    IReadOnlyList<ReportFinding>? Findings = null,
    IReadOnlyList<ReportEvidenceReference>? EvidenceReferences = null,
    ReadinessScoreRepresentation? Readiness = null);

public sealed record ReadinessScoreRepresentation(
    ReadinessOverallStatus OverallStatus,
    double? OverallRawScore0To100,
    int? OverallDisplayScore0To100,
    ReadinessMaturityClassification? OverallClassification,
    IReadOnlyList<ReadinessDimensionRepresentation> Dimensions);

public sealed record ReadinessDimensionRepresentation(
    string DimensionId,
    ReadinessDimensionStatus Status,
    double? RawScore0To100,
    int? DisplayScore0To100,
    ReadinessMaturityClassification? Classification);

public sealed record IndustrialDataAndSystemsSection(
    ReportAssessmentState State = ReportAssessmentState.NotAssessed,
    IReadOnlyList<SystemAssessmentEntry>? Systems = null,
    IReadOnlyList<ReportFinding>? Findings = null,
    IReadOnlyList<ReportEvidenceReference>? EvidenceReferences = null);

public sealed record SystemAssessmentEntry(
    string Id,
    string Name,
    ReportAssessmentState AssessmentState,
    string? SystemType,
    string? Owner,
    string? OperationalArea,
    string? DataExchanged,
    string? IntegrationPattern,
    string? Criticality,
    ReportContentProvenance Provenance,
    ReportVerificationStatus VerificationStatus,
    IReadOnlyList<ReportEvidenceReference> EvidenceReferences,
    string? ConsultantNotes = null);

public sealed record AiOpportunitiesAndRoiSection(
    ReportAssessmentState State = ReportAssessmentState.NotAssessed,
    IReadOnlyList<AiImplementationOpportunity>? Opportunities = null,
    IReadOnlyList<ReportFinding>? Findings = null);

public sealed record AiImplementationOpportunity(
    string Id,
    string BusinessProblem,
    string OperationalProcessOrArea,
    string ProposedUseCase,
    string? ExpectedMeasurableOutcome,
    IReadOnlyList<string> RequiredDataSources,
    IReadOnlyList<string> TechnicalPrerequisites,
    IReadOnlyList<string> ImplementationDependencies,
    IReadOnlyList<string> RisksAndAssumptions,
    EffortCostEstimate Estimate,
    ImplementationScope Scope,
    ReportContentProvenance Provenance,
    ReportVerificationStatus VerificationStatus,
    IReadOnlyList<ReportEvidenceReference> EvidenceReferences);

public sealed record EffortCostEstimate(
    ReportEstimateStatus Status,
    decimal? EffortValue = null,
    string? EffortUnit = null,
    decimal? CostAmount = null,
    string? Currency = null,
    string? Basis = null,
    IReadOnlyList<string>? Assumptions = null,
    IReadOnlyList<ReportEvidenceReference>? EvidenceReferences = null);

public sealed record ImplementationScope(
    ReportAssessmentState State,
    string? Description = null,
    IReadOnlyList<string>? InScopeItems = null,
    IReadOnlyList<string>? OutOfScopeItems = null);

public sealed record TechnicalGapsAndArchitectureSection(
    ReportAssessmentState State = ReportAssessmentState.NotAssessed,
    IReadOnlyList<TechnicalGapEntry>? Gaps = null,
    IReadOnlyList<ArchitectureRecommendation>? Recommendations = null);

public sealed record TechnicalGapEntry(
    string Id,
    string Title,
    string Description,
    string Impact,
    ReportContentProvenance Provenance,
    ReportVerificationStatus VerificationStatus,
    IReadOnlyList<ReportEvidenceReference> EvidenceReferences,
    bool RequiresValidation = false);

public sealed record ArchitectureRecommendation(
    string Id,
    string Title,
    string Recommendation,
    string Rationale,
    ReportContentProvenance Provenance,
    ReportVerificationStatus VerificationStatus,
    IReadOnlyList<ReportEvidenceReference> EvidenceReferences,
    bool RequiresValidation = false);

public sealed record ImplementationRoadmapSection(
    ReportAssessmentState State = ReportAssessmentState.NotAssessed,
    IReadOnlyList<RoadmapItem>? Items = null);

public sealed record RoadmapItem(
    string Id,
    string Title,
    string Activities,
    int StartDay,
    int EndDay,
    int Priority,
    string? OwnerRole,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Assumptions,
    string? SuccessMeasure,
    ReportContentProvenance Provenance,
    ReportVerificationStatus VerificationStatus,
    IReadOnlyList<ReportEvidenceReference> EvidenceReferences);

public sealed record ExecutivePresentationSection(
    ReportAssessmentState State = ReportAssessmentState.NotAssessed,
    string? Title = null,
    string? Narrative = null,
    IReadOnlyList<ReportTextEntry>? KeyMessages = null,
    IReadOnlyList<ReportTextEntry>? DecisionsRequested = null,
    IReadOnlyList<string>? TopRisks = null,
    IReadOnlyList<ReportEvidenceReference>? EvidenceReferences = null);

public static class ConsultantReportContentSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(ConsultantReportContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureSupportedVersion(content.SchemaVersion);
        return JsonSerializer.Serialize(content, Options);
    }

    public static ConsultantReportContent Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var content = JsonSerializer.Deserialize<ConsultantReportContent>(json, Options)
            ?? throw new JsonException("Consultant report content is empty.");
        EnsureSupportedVersion(content.SchemaVersion);
        return content;
    }

    private static void EnsureSupportedVersion(string? schemaVersion)
    {
        if (!string.Equals(schemaVersion, ConsultantReportContent.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported consultant report content schema version '{schemaVersion}'.");
        }
    }
}
