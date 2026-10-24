using System.Text.Json;
using System.Text.RegularExpressions;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using UglyToad.PdfPig;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class ReportTemplateV2Tests
{
    private static readonly DateTime Issued = new(2026, 10, 20, 14, 30, 0, DateTimeKind.Utc);
    private static readonly ReportPdfTemplateRegistry Registry = new();

    private static string Squash(string value) => Regex.Replace(value, @"\s+", string.Empty);

    private static ReadinessItemSnapshot Item(string q, string? f, string? value, int? score, ReadinessItemStatus status = ReadinessItemStatus.Scoreable)
        => new(q, f, f is null ? q : $"{q}.{f}", value, status, score, score * 25, null, null);

    private static ReadinessScoringSnapshot Snapshot(string version, bool analyticsMaxed = true, bool withheldD1 = false)
    {
        var d3 = new ReadinessDimensionSnapshot("D3", "Analytics & Reporting", 0.25m, ReadinessDimensionStatus.Eligible, 100, 100,
            ReadinessMaturityClassification.Advanced, 3, 3, 1.0,
            new[]
            {
                Item("q24", null, "automated_validated", 4),
                Item("q31", null, "consistently_accurate", 4),
                Item("q32", null, "proactive_decisions", 4)
            }, Array.Empty<ReadinessScoreReason>());
        var d1 = new ReadinessDimensionSnapshot("D1", "Connectivity & Integration", 0.30m,
            withheldD1 ? ReadinessDimensionStatus.Withheld : ReadinessDimensionStatus.Eligible,
            withheldD1 ? null : 50, withheldD1 ? null : 50,
            withheldD1 ? null : ReadinessMaturityClassification.Established, 1, 3, 0.33,
            new[] { Item("q15", "integration_level", "partially", 2) },
            withheldD1 ? new[] { ReadinessScoreReason.InsufficientScoreableItems } : Array.Empty<ReadinessScoreReason>());
        var d2 = new ReadinessDimensionSnapshot("D2", "Data Quality & Contextualization", 0.45m, ReadinessDimensionStatus.Eligible, 50, 50,
            ReadinessMaturityClassification.Established, 4, 4, 1.0,
            new[] { Item("q17", "history_choice", "some_sources", 2), Item("q19", null, "partial", 2) }, Array.Empty<ReadinessScoreReason>());
        var overallOk = !withheldD1;
        return new ReadinessScoringSnapshot(version,
            overallOk ? ReadinessOverallStatus.Eligible : ReadinessOverallStatus.Withheld,
            overallOk ? 60 : null, overallOk ? 60 : null, overallOk ? ReadinessMaturityClassification.Established : null,
            ReadinessEvidenceProvenance.SelfReported, new[] { d1, d2, d3 },
            overallOk ? Array.Empty<ReadinessScoreReason>() : new[] { ReadinessScoreReason.OverallRequiresAllDimensions },
            Array.Empty<ReadinessReviewFlagSnapshot>());
    }

    private static ConsultantReportContent Content(ReadinessScoringSnapshot snapshot, bool complete)
    {
        var empty = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.NotAssessed, null, null, null,
                ReportPdfRenderingTestsAccess.Readiness(snapshot))
        };
        if (!complete)
        {
            return empty;
        }

        var evidence = new[]
        {
            new ReportEvidenceReference(ReportEvidenceKind.ScoringItem, "q24", "q24", null, "q24"),
            new ReportEvidenceReference(ReportEvidenceKind.ScoringItem, "q31", "q31", null, "q31"),
            new ReportEvidenceReference(ReportEvidenceKind.ScoringItem, "q15.integration_level", "q15", "integration_level", "q15.integration_level")
        };
        return empty with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed, "Summary narrative.",
                new[]
                {
                    new ReportFinding("f1", "A finding", "Finding narrative.", ReportFindingClassification.PreliminaryFinding,
                        ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified, evidence)
                }, null, ReportPdfRenderingTestsAccess.Readiness(snapshot)),
            ExecutivePresentation = new ExecutivePresentationSection(ReportAssessmentState.Assessed, "S", "N",
                Array.Empty<ReportTextEntry>(),
                new[] { new ReportTextEntry("d1", "Approve the pilot scope.", ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.SelfReported, evidence) },
                Array.Empty<string>(), null)
        };
    }

    private static ReportPdfRenderModel Model(string questionnaireVersion, ReadinessScoringSnapshot snapshot, ConsultantReportContent content,
        string template = ReportTemplateVersions.V2, Guid? basedOn = null)
    {
        var report = new AssessmentReport
        {
            Id = Guid.NewGuid(), AssessmentId = Guid.NewGuid(), QuestionnaireVersion = questionnaireVersion,
            ResponseSubmittedUtc = Issued.AddDays(-10)
        };
        var revision = new AssessmentReportRevision
        {
            Id = Guid.NewGuid(), AssessmentReportId = report.Id, RevisionNumber = basedOn is null ? 1 : 2,
            Status = AssessmentReportRevisionStatus.Issued, ScoringContractVersion = "contract-1",
            ScoringSnapshotSchemaVersion = ReadinessScoringSnapshotSerializer.PersistedSchemaVersion,
            ScoringSnapshot = JsonSerializer.Serialize(new ReadinessScoringSnapshotEnvelope("1.0", snapshot), ReportPdfRenderingTests.SnapshotJson),
            ConsultantContentJson = ConsultantReportContentSerializer.Serialize(content),
            ConsultantContentSchemaVersion = "1.0", ReportTemplateVersion = template,
            ApprovedUtc = Issued.AddHours(-1), ApprovedByUserId = "approver", IssuedUtc = Issued, IssuedByUserId = "issuer",
            BasedOnRevisionId = basedOn, CorrectionReason = basedOn is null ? null : "Corrected a recorded value."
        };
        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "Fictional Assessment", "Fictional Plant"));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Model!;
    }

    private static string Text(ReportPdfRenderModel model, string? saveAs = null)
    {
        var pdf = new ReportPdfRenderer(Registry).Render(model);
        var dir = Environment.GetEnvironmentVariable("SENTINEL_VALIDATION_PDF_DIR");
        if (saveAs is not null && !string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, saveAs + ".pdf"), pdf);
        }

        using var document = PdfDocument.Open(pdf);
        var text = string.Join("\n", document.GetPages().Select(page => page.Text));
        if (saveAs is not null && !string.IsNullOrWhiteSpace(dir))
        {
            File.WriteAllText(Path.Combine(dir, saveAs + ".txt"), text);
        }

        return Regex.Replace(text, "\\s+", string.Empty);
    }

    [Fact]
    public void Catalog_Uses_Wording_Of_The_Recorded_Questionnaire_Version()
    {
        var v3 = ReportEvidenceCatalog.Find("3.0", "q33", null, "none");
        Assert.NotNull(v3);
        Assert.Equal("None", v3!.AnswerLabel);

        var v1 = ReportEvidenceCatalog.Find("1.0", "q24", null, "automated_validated");
        Assert.Equal("Automated and validated", v1!.AnswerLabel);
        Assert.Equal("How are operational KPIs calculated and validated?", v1.Question);

        var composite = ReportEvidenceCatalog.Find("3.0", "q15", "integration_level", "partially");
        Assert.Equal("Partially integrated", composite!.AnswerLabel);
    }

    [Fact]
    public void Catalog_Does_Not_Apply_Later_Version_Wording_To_Earlier_Versions()
    {
        Assert.Null(ReportEvidenceCatalog.Find("1.0", "q31", null, "consistently_accurate"));
        Assert.Null(ReportEvidenceCatalog.Find("2.0", "q33", null, "none"));
        Assert.Null(ReportEvidenceCatalog.Find("9.9", "q24", null, "automated_validated"));
        Assert.Null(ReportEvidenceCatalog.Find(null, "q24", null, "x"));
    }

    [Fact]
    public void Catalog_Unknown_Identifiers_And_Answer_Codes_Fall_Back_Safely()
    {
        Assert.Null(ReportEvidenceCatalog.Find("3.0", "q999", null, "x"));
        Assert.Null(ReportEvidenceCatalog.Find("3.0", "q15", "nope", "x"));
        var unknownAnswer = ReportEvidenceCatalog.Find("3.0", "q24", null, "not_a_code");
        Assert.NotNull(unknownAnswer);
        Assert.Null(unknownAnswer!.AnswerLabel);
        Assert.Null(ReportEvidenceCatalog.FindDimension("1.0", "D3"));
        Assert.Null(ReportEvidenceCatalog.FindDimension("3.0", "D9"));
    }

    [Fact]
    public void V2_Renders_Historical_Wording_And_Score_Interpretation_For_V3()
    {
        var snapshot = Snapshot("3.0");
        var text = Text(Model("3.0", snapshot, Content(snapshot, true)), "standard-v3");

        Assert.Contains(Squash("How are operational KPIs calculated and validated?"), text);
        Assert.Contains(Squash("Automated and validated"), text);
        Assert.Contains(Squash("weighted average"), text);
        Assert.Contains(Squash("Analytics & Reporting"), text);
        Assert.Contains(Squash("whether analytics cover all equipment, lines or sites"), text);
        Assert.Contains(Squash("Approve the pilot scope."), text);
        Assert.Contains(Squash("Classification bands"), text);
    }

    [Fact]
    public void V2_Uses_Explicit_Fallback_When_Questionnaire_Version_Has_No_Wording()
    {
        var snapshot = Snapshot("1.0");
        var text = Text(Model("1.0", snapshot, Content(snapshot, true)));

        Assert.Contains(Squash("Question wording is not available for questionnaire version 1.0"), text);
        Assert.DoesNotContain(Squash("consistently accurate and trusted"), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V2_Withheld_Scores_Show_No_Overall_Score()
    {
        var snapshot = Snapshot("3.0", withheldD1: true);
        var text = Text(Model("3.0", snapshot, Content(snapshot, false)), "withheld-v3");

        Assert.Contains(Squash("Not scored (withheld)"), text);
        Assert.DoesNotContain(Squash("60 / 100"), text);
        Assert.DoesNotContain(Squash("Classification bands"), text);
    }

    [Fact]
    public void V2_Missing_Consultant_Decisions_Omits_Empty_Heading_And_Invents_Nothing()
    {
        var snapshot = Snapshot("3.0");
        var text = Text(Model("3.0", snapshot, Content(snapshot, false)), "incomplete-v3");

        Assert.DoesNotContain(Squash("Decisions requested"), text);
        Assert.Contains(Squash("No decisions or roadmap actions have been recorded"), text);
        Assert.DoesNotContain(Squash("ROI of"), text);
    }

    [Fact]
    public void Corrected_Revision_Retains_V2_And_V1_Remains_Resolvable()
    {
        var snapshot = Snapshot("3.0");
        var model = Model("3.0", snapshot, Content(snapshot, true), basedOn: Guid.NewGuid());
        Assert.Equal(ReportTemplateVersions.V2, model.TemplateVersion);
        var text = Text(model, "corrected-v3");
        Assert.Contains(Squash("Corrected revision"), text);

        Assert.True(Registry.Resolve(ReportTemplateVersions.V1).IsSupported);
        var v1Snapshot = Snapshot("3.0");
        var v1 = Text(Model("3.0", v1Snapshot, Content(v1Snapshot, true), ReportTemplateVersions.V1));
        Assert.DoesNotContain(Squash("How to read these scores"), v1);
    }

    [Fact]
    public void Unsupported_Template_Version_Is_Rejected_Without_Fallback()
    {
        var resolution = Registry.Resolve("sentinel-assessment-report/99");
        Assert.False(resolution.IsSupported);
        Assert.Null(resolution.Template);
    }
}

internal static class ReportPdfRenderingTestsAccess
{
    public static ReadinessScoreRepresentation Readiness(ReadinessScoringSnapshot s) => ReportPdfRenderingTests.Readiness(s);
}
