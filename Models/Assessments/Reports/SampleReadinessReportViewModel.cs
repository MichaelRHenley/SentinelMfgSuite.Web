namespace SentinelMfgSuite.Web.Models.Assessments.Reports;

public sealed class SampleReadinessReportViewModel
{
    public string ReportTitle { get; init; } = "Industrial Data & AI Readiness Assessment";
    public string CustomerName { get; init; } = "Northstar Components Ltd. (Fictional)";
    public string SiteName { get; init; } = "Lakeview Manufacturing Centre (Fictional)";
    public string ReportDate { get; init; } = "October 2026";
    public string Revision { get; init; } = "Sample revision 1.0";
    public string Confidentiality { get; init; } = "CONFIDENTIAL — SAMPLE / NOT FOR CUSTOMER DISTRIBUTION";
    public string OverallScore { get; init; } = "67 / 100";
    public string OverallMaturity { get; init; } = "Established";
    public string ScenarioLabel { get; init; } = "ILLUSTRATIVE V3 SCORING SCENARIO — NOT CALCULATED FROM A SUBMITTED RESPONSE";
    public string ExecutiveSummary { get; init; } = "This fictional example depicts a manufacturer with a useful foundation for analytics, alongside inconsistent operational context and manual reporting effort. The illustrative V3 score is eligible under its evidence gates; it is not a verified statement about any real organization.";

    public IReadOnlyList<SampleReportDimension> Dimensions { get; init; } = Array.Empty<SampleReportDimension>();
    public IReadOnlyList<string> ScopeItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Opportunities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SampleReportRecommendation> Recommendations { get; init; } = Array.Empty<SampleReportRecommendation>();
    public IReadOnlyList<SampleReportRoadmapPhase> Roadmap { get; init; } = Array.Empty<SampleReportRoadmapPhase>();
    public IReadOnlyList<string> AssumptionsAndExclusions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InsufficientEvidenceExamples { get; init; } = Array.Empty<string>();

    public static SampleReadinessReportViewModel Create() => new()
    {
        Dimensions = new[]
        {
            new SampleReportDimension("D1", "Connectivity & Integration", 30, "87.5", 88, "Advanced", "2 / 3 (67%)", "Strong reported connectivity and reliability; validate coverage against the site’s critical production flows."),
            new SampleReportDimension("D2", "Data Quality & Contextualization", 45, "62.5", 63, "Established", "4 / 6 (67%)", "Reported controls are developing; identifier consistency and completeness practices warrant focused validation."),
            new SampleReportDimension("D3", "Analytics & Reporting", 25, "50", 50, "Established", "2 / 3 (67%)", "Reporting practices provide a base for improvement, while KPI validation remains incomplete in this fictional scenario.")
        },
        ScopeItems = new[]
        {
            "One fictional manufacturing site; remote assessment delivery.",
            "Up to five data sources considered for the engagement scope.",
            "Questionnaire-led review, consultant interpretation, prioritized recommendations, and a findings-review meeting.",
            "Typical target turnaround: 1–2 weeks after the agreed assessment inputs are available."
        },
        Findings = new[]
        {
            "Fictional scenario: production, quality, and maintenance systems exchange some data, but selected workflows still depend on manual exports.",
            "Fictional scenario: equipment identifiers vary between source systems, adding reconciliation effort to cross-line reporting.",
            "Fictional scenario: KPI definitions and validation checks are not consistently documented across operational teams."
        },
        Opportunities = new[]
        {
            "Prioritize a small, governed set of equipment and production-order identifiers across the in-scope sources.",
            "Replace recurring manual transfers for one high-value workflow with a monitored, supportable integration pattern.",
            "Establish data ownership, quality checks, and contextual metadata before expanding analytics or AI use cases."
        },
        Recommendations = new[]
        {
            new SampleReportRecommendation("P1", "Agree on a shared equipment and production context model", "Define canonical asset, line, product, and order identifiers for the selected use case; map source-specific values and assign owners.", "Reduces reconciliation effort and creates reusable context for reporting and future analytics."),
            new SampleReportRecommendation("P2", "Validate operational KPI definitions and controls", "Document KPI formulas, source fields, refresh expectations, and exception handling; review results with operations and finance stakeholders.", "Improves confidence in operational reporting and makes data-quality gaps visible."),
            new SampleReportRecommendation("P3", "Pilot one monitored integration workflow", "Select one priority source-to-consumer flow, define access and OT/IT security controls, and instrument freshness and failure monitoring.", "Tests integration value and operating responsibility before broader rollout.")
        },
        Roadmap = new[]
        {
            new SampleReportRoadmapPhase("Days 1–30", "Align and baseline", "Confirm business outcome, owners, source boundaries, identifier conventions, KPI definitions, and baseline measures."),
            new SampleReportRoadmapPhase("Days 31–60", "Pilot and validate", "Implement one bounded integration or data-quality pilot; test reconciliation, access controls, monitoring, and stakeholder acceptance."),
            new SampleReportRoadmapPhase("Days 61–90", "Operationalize and prioritize next", "Review pilot outcomes, formalize support ownership, refine the roadmap, and assess whether a constrained analytics/AI use case is ready.")
        },
        AssumptionsAndExclusions = new[]
        {
            "All customer, site, operating, and scoring details in this document are fictional and illustrative.",
            "Questionnaire-derived statements are self-reported and have not been independently verified; evidence coverage is not a measure of confidence.",
            "This sample is not a security assessment, engineering design, compliance opinion, implementation commitment, or guarantee of business outcomes.",
            "Actual engagement scope, source availability, dependencies, and timing must be agreed with the customer."
        },
        InsufficientEvidenceExamples = new[]
        {
            "Overall readiness: Insufficient Evidence — at least one required dimension does not meet its V3 evidence gate.",
            "D1 Connectivity & Integration: Insufficient Evidence — only 1 of 3 eligible items is scoreable; the minimum is 2, including q33 or q34.",
            "No numeric score or maturity classification is assigned to a withheld result. This is a separate illustrative presentation state, not part of the eligible scenario above."
        }
    };
}

public sealed record SampleReportDimension(
    string Id,
    string Name,
    int WeightPercent,
    string RawScore,
    int Score,
    string Maturity,
    string EvidenceCoverage,
    string Interpretation);

public sealed record SampleReportRecommendation(string Priority, string Title, string Action, string Rationale);

public sealed record SampleReportRoadmapPhase(string Period, string Title, string Activities);