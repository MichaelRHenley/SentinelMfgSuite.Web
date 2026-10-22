using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using UglyToad.PdfPig;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class ReportPdfRenderingTests
{
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly DateTime Issued = new(2026, 10, 20, 14, 30, 0, DateTimeKind.Utc);

    private static readonly ReportPdfTemplateRegistry Registry = new();

    // ------------------------------------------------------------ fixtures

    internal static ReadinessScoringSnapshot Snapshot(bool withheld = false) => new(
        "1.0",
        withheld ? ReadinessOverallStatus.Withheld : ReadinessOverallStatus.Eligible,
        withheld ? null : 62.5,
        withheld ? null : 63,
        withheld ? null : ReadinessMaturityClassification.Established,
        ReadinessEvidenceProvenance.SelfReported,
        new[]
        {
            new ReadinessDimensionSnapshot(
                "data", "Data Foundations", 0.5m, ReadinessDimensionStatus.Eligible, 71.2, 71,
                ReadinessMaturityClassification.Established, 2, 3, 0.66,
                new[]
                {
                    new ReadinessItemSnapshot("q-historian", null, "historian", "Partial", ReadinessItemStatus.Scoreable, 2, 50, 12.5, null),
                    new ReadinessItemSnapshot("q-mes", "f1", "mes", null, ReadinessItemStatus.Missing, null, null, null, "missing")
                },
                Array.Empty<ReadinessScoreReason>()),
            new ReadinessDimensionSnapshot(
                "people", "Skills and Governance", 0.5m,
                withheld ? ReadinessDimensionStatus.Withheld : ReadinessDimensionStatus.Eligible,
                withheld ? null : 54.0, withheld ? null : 54,
                withheld ? null : ReadinessMaturityClassification.Developing, 1, 4, 0.25,
                Array.Empty<ReadinessItemSnapshot>(),
                withheld ? new[] { ReadinessScoreReason.InsufficientScoreableItems } : Array.Empty<ReadinessScoreReason>())
        },
        withheld ? new[] { ReadinessScoreReason.OverallRequiresAllDimensions } : Array.Empty<ReadinessScoreReason>(),
        new[] { new ReadinessReviewFlagSnapshot("FLAG-1", "Informational", "Historian value needs review.", new[] { "q-historian" }, new[] { "Partial" }) });

    internal static ReadinessScoreRepresentation Readiness(ReadinessScoringSnapshot s) => new(
        s.OverallStatus, s.OverallRawScore0To100, s.OverallDisplayScore0To100, s.OverallClassification,
        s.Dimensions.Select(d => new ReadinessDimensionRepresentation(
            d.DimensionId, d.Status, d.RawScore0To100, d.DisplayScore0To100, d.Classification)).ToArray());

    internal static ConsultantReportContent Content(ReadinessScoringSnapshot snapshot)
    {
        var evidence = new[]
        {
            new ReportEvidenceReference(ReportEvidenceKind.ScoringItem, "q-historian:historian", "q-historian", null, "historian"),
            new ReportEvidenceReference(ReportEvidenceKind.ScoringDimension, "data")
        };

        return ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(
                ReportAssessmentState.Assessed,
                "Historian coverage is partial across the site.",
                new[]
                {
                    new ReportFinding("f1", "Partial historian coverage", "Only part of the line is historised.",
                        ReportFindingClassification.PreliminaryFinding, ReportContentProvenance.ConsultantEntered,
                        ReportVerificationStatus.Unverified, evidence)
                },
                null,
                Readiness(snapshot)),
            TechnicalGapsAndArchitecture = new TechnicalGapsAndArchitectureSection(
                ReportAssessmentState.Assessed,
                new[]
                {
                    new TechnicalGapEntry("g1", "No MES integration", "MES data is not available to analytics.", "Limits root-cause analysis.",
                        ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.SelfReported, evidence, true)
                },
                new[]
                {
                    new ArchitectureRecommendation("r1", "Introduce a unified namespace", "Publish line data to a broker.", "Reduces point-to-point links.",
                        ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.ConsultantVerified, evidence)
                }),
            ImplementationRoadmap = new ImplementationRoadmapSection(
                ReportAssessmentState.Assessed,
                new[]
                {
                    new RoadmapItem("m2", "Pilot analytics", "Run a pilot on one line.", 31, 60, 2, "Data lead",
                        new[] { "m1" }, new[] { "Pilot report delivered" }, Array.Empty<string>(), "Pilot accepted",
                        ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.SelfReported, evidence),
                    new RoadmapItem("m1", "Stabilise historian", "Close coverage gaps.", 1, 30, 1, "Controls engineer",
                        Array.Empty<string>(), new[] { "Coverage confirmed" }, new[] { "Line access granted" }, null,
                        ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.SelfReported, evidence)
                }),
            ExecutivePresentation = new ExecutivePresentationSection(
                ReportAssessmentState.Assessed, "Summary", "Narrative",
                new[] { new ReportTextEntry("k1", "Data foundations are the constraint.", ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.SelfReported, evidence) },
                new[] { new ReportTextEntry("d1", "Approve historian remediation.", ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.SelfReported, evidence) },
                new[] { "Unplanned downtime" },
                null)
        };
    }

    private static (AssessmentReport Report, AssessmentReportRevision Revision) Seed(
        ReadinessScoringSnapshot? snapshot = null,
        ConsultantReportContent? content = null,
        string? template = "sentinel-assessment-report/1",
        Guid? basedOn = null,
        string? correctionReason = null)
    {
        snapshot ??= Snapshot();
        content ??= Content(snapshot);
        var report = new AssessmentReport
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AssessmentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            QuestionnaireVersion = "1.0",
            ResponseSubmittedUtc = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)
        };
        var revision = new AssessmentReportRevision
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            AssessmentReportId = report.Id,
            RevisionNumber = basedOn is null ? 1 : 2,
            Status = AssessmentReportRevisionStatus.Issued,
            ScoringContractVersion = "contract-1",
            ScoringSnapshotSchemaVersion = ReadinessScoringSnapshotSerializer.PersistedSchemaVersion,
            ScoringSnapshot = JsonSerializer.Serialize(new ReadinessScoringSnapshotEnvelope("1.0", snapshot), SnapshotJson),
            ConsultantContentJson = ConsultantReportContentSerializer.Serialize(content),
            ConsultantContentSchemaVersion = "1.0",
            ReportTemplateVersion = template,
            ApprovedUtc = Issued.AddHours(-1),
            ApprovedByUserId = "approver",
            IssuedUtc = Issued,
            IssuedByUserId = "issuer",
            BasedOnRevisionId = basedOn,
            CorrectionReason = correctionReason
        };
        return (report, revision);
    }

    internal static ReportPdfRenderModel Model(
        ReadinessScoringSnapshot? snapshot = null,
        ConsultantReportContent? content = null,
        string? template = "sentinel-assessment-report/1",
        Guid? basedOn = null,
        string? correctionReason = null)
    {
        var (report, revision) = Seed(snapshot, content, template, basedOn, correctionReason);
        var result = new ReportPdfModelBuilder(Registry).Build(
            new ReportPdfSource(report, revision, "Plant Readiness 2026", "Springfield Plant"));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Model!;
    }

    internal static byte[] Render(ReportPdfRenderModel model) => new ReportPdfRenderer(Registry).Render(model);

    private static string ExtractText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }

    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static void AssertContains(string text, string expected)
        => Assert.Contains(Normalize(expected), Normalize(text), StringComparison.Ordinal);

    private static void AssertNotContains(string text, string unexpected)
        => Assert.DoesNotContain(Normalize(unexpected), Normalize(text), StringComparison.Ordinal);

    // --------------------------------------------------------------- tests

    [Fact]
    public void Render_Produces_Valid_Pdf_With_Stable_Metadata()
    {
        var pdf = Render(Model());

        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        using var document = PdfDocument.Open(pdf);
        Assert.True(document.NumberOfPages >= 2);
        Assert.Equal("Sentinel AI Systems", document.Information.Author);
        Assert.Contains("Revision 1", document.Information.Title);
        Assert.Contains("2026", document.Information.CreationDate);
    }

    [Fact]
    public void Render_Includes_All_Required_Sections()
    {
        var text = ExtractText(Render(Model()));

        foreach (var heading in new[]
        {
            "SENTINEL AI SYSTEMS", "AI Readiness Assessment", "Plant Readiness 2026", "Springfield Plant",
            "1. Executive Summary", "2. Scope and Methodology", "3. Overall AI Readiness",
            "4. Readiness Dimensions and Scores", "5. Key Findings and Evidence",
            "6. Operational and Technical Gaps", "7. Prioritized Recommendations",
            "8. Implementation Roadmap", "9. Assumptions and Limitations", "10. Next Steps",
            "11. Report Version and Issuance"
        })
        {
            AssertContains(text, heading);
        }

        AssertContains(text, "Page 2 of");
    }

    [Fact]
    public void Render_Uses_Frozen_Scoring_Values()
    {
        var text = ExtractText(Render(Model()));

        AssertContains(text, "63 / 100");
        AssertContains(text, "Established");
        AssertContains(text, "Data Foundations");
        AssertContains(text, "71 / 100");
        AssertContains(text, "Skills and Governance");
        AssertContains(text, "contract-1");
        AssertContains(text, "Questionnaire version");
    }

    [Fact]
    public void Render_Does_Not_Present_Withheld_Scores_As_Numbers()
    {
        var text = ExtractText(Render(Model(Snapshot(withheld: true))));

        AssertContains(text, "Not scored (withheld)");
        AssertContains(text, "Withheld");
        AssertNotContains(text, "63 / 100");
        AssertNotContains(text, "54 / 100");
        AssertContains(text, "Overall requires all dimensions");
        AssertContains(text, "Skills and Governance score withheld");
    }

    [Fact]
    public void Render_Shows_Unavailable_Statements_For_Missing_Optional_Content()
    {
        var snapshot = Snapshot();
        var empty = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.NotAssessed, null, null, null, Readiness(snapshot))
        };
        var text = ExtractText(Render(Model(snapshot, empty)));

        AssertContains(text, "Not provided in this revision.");
        AssertContains(text, "No findings are recorded in this revision.");
        AssertContains(text, "Section state: Not assessed.");
        AssertContains(text, "Executive summary: Not assessed.");
    }

    [Fact]
    public void Render_Preserves_Finding_Classifications_And_Verification_Status()
    {
        var text = ExtractText(Render(Model()));

        AssertContains(text, "Preliminary finding");
        AssertContains(text, "Unverified");
        AssertContains(text, "1 finding(s) are classified as preliminary.");
        AssertContains(text, "Requires validation");
    }

    [Fact]
    public void Render_Resolves_Evidence_Only_From_Frozen_Snapshot()
    {
        var text = ExtractText(Render(Model()));

        AssertContains(text, "recorded value 'Partial'");
        AssertContains(text, "Scoring dimension: Data Foundations, score 71/100");
    }

    [Fact]
    public void Render_Marks_Unresolvable_Evidence_As_Not_Frozen()
    {
        var snapshot = Snapshot();
        var content = Content(snapshot);
        var finding = content.ExecutiveSummary.Findings![0] with
        {
            EvidenceReferences = new[] { new ReportEvidenceReference(ReportEvidenceKind.QuestionnaireAnswer, "q-unknown", "q-unknown") }
        };
        content = content with { ExecutiveSummary = content.ExecutiveSummary with { Findings = new[] { finding } } };

        var text = ExtractText(Render(Model(snapshot, content)));

        AssertContains(text, "value not frozen in this revision");
    }

    [Fact]
    public void Render_Orders_Roadmap_By_Priority()
    {
        var text = Normalize(ExtractText(Render(Model())));

        Assert.True(text.IndexOf(Normalize("Priority 1: Stabilise historian"), StringComparison.Ordinal)
            < text.IndexOf(Normalize("Priority 2: Pilot analytics"), StringComparison.Ordinal));
    }

    [Fact]
    public void Render_Correction_Revision_Shows_Lineage()
    {
        var basedOn = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var text = ExtractText(Render(Model(basedOn: basedOn, correctionReason: "Corrected historian coverage figure.")));

        AssertContains(text, "Corrected revision");
        AssertContains(text, "Corrects revision");
        AssertContains(text, basedOn.ToString("D"));
        AssertContains(text, "Corrected historian coverage figure.");
    }

    [Fact]
    public void Render_Is_Deterministic_For_Identical_Inputs()
    {
        var model = Model();

        var first = Render(model);
        var second = Render(model);
        var third = Render(Model());

        var h1 = Convert.ToHexString(SHA256.HashData(first));
        var h2 = Convert.ToHexString(SHA256.HashData(second));
        var h3 = Convert.ToHexString(SHA256.HashData(third));
        Assert.Equal(h1, h2);
        Assert.Equal(h1, h3);
        Assert.Equal(first.Length, second.Length);
    }

    [Fact]
    public void Render_Output_Changes_When_Frozen_Content_Changes()
    {
        var snapshot = Snapshot();
        var changed = Content(snapshot) with
        {
            ExecutiveSummary = Content(snapshot).ExecutiveSummary with { Narrative = "Different narrative." }
        };

        Assert.NotEqual(
            Convert.ToHexString(SHA256.HashData(Render(Model()))),
            Convert.ToHexString(SHA256.HashData(Render(Model(snapshot, changed)))));
    }

    // ----------------------------------------------------- template version

    [Fact]
    public void Registry_Resolves_V1_Explicitly()
    {
        var resolution = Registry.Resolve("sentinel-assessment-report/1");

        Assert.True(resolution.IsSupported);
        Assert.False(resolution.IsLegacyNullCompatibility);
        Assert.Equal(ReportTemplateVersions.V1, resolution.ResolvedVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Registry_Resolves_Legacy_Null_To_V1(string? persisted)
    {
        var resolution = Registry.Resolve(persisted);

        Assert.True(resolution.IsSupported);
        Assert.True(resolution.IsLegacyNullCompatibility);
        Assert.Equal(ReportTemplateVersions.V1, resolution.ResolvedVersion);
    }

    [Fact]
    public void Registry_Rejects_Unknown_Versions_Without_Fallback()
    {
        var resolution = Registry.Resolve("sentinel-assessment-report/2");

        Assert.False(resolution.IsSupported);
        Assert.Null(resolution.Template);
    }

    [Fact]
    public void Builder_Renders_Legacy_Null_Template_As_V1_Without_Mutating_Revision()
    {
        var (report, revision) = Seed(template: null);

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.True(result.Succeeded);
        Assert.Equal(ReportTemplateVersions.V1, result.Model!.TemplateVersion);
        Assert.True(result.Model.TemplateVersionIsLegacyNullCompatibility);
        Assert.Null(revision.ReportTemplateVersion);
    }

    [Fact]
    public void Builder_Fails_For_Unknown_Template_Version()
    {
        var (report, revision) = Seed(template: "sentinel-assessment-report/9");

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "template-unsupported");
    }

    [Fact]
    public void Renderer_Throws_For_Model_With_Unknown_Template()
    {
        var model = Model() with { TemplateVersion = "sentinel-assessment-report/9" };

        Assert.Throws<ReportPdfTemplateUnsupportedException>(() => Render(model));
    }

    // ------------------------------------------------------ source validation

    [Fact]
    public void Builder_Fails_For_Unsupported_Content_Schema()
    {
        var (report, revision) = Seed();
        revision.ConsultantContentJson = revision.ConsultantContentJson!.Replace("\"schemaVersion\":\"1.0\"", "\"schemaVersion\":\"9.9\"");

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "content-unsupported");
    }

    [Fact]
    public void Builder_Fails_For_Unsupported_Snapshot_Schema_Column()
    {
        var (report, revision) = Seed();
        revision.ScoringSnapshotSchemaVersion = 99;

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "scoring-snapshot-unsupported");
    }

    [Fact]
    public void Builder_Fails_For_Unsupported_Snapshot_Envelope_Version()
    {
        var (report, revision) = Seed();
        revision.ScoringSnapshot = revision.ScoringSnapshot!.Replace("\"schemaVersion\":\"1.0\"", "\"schemaVersion\":\"9.9\"");

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "scoring-snapshot-unsupported");
    }

    [Fact]
    public void Builder_Fails_For_Missing_Content_Or_Snapshot()
    {
        var (report, revision) = Seed();
        revision.ConsultantContentJson = null;
        Assert.Contains(new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S")).Errors,
            e => e.Code == "content-missing");

        (report, revision) = Seed();
        revision.ScoringSnapshot = null;
        Assert.Contains(new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S")).Errors,
            e => e.Code == "scoring-snapshot-missing");
    }

    [Fact]
    public void Builder_Fails_When_Questionnaire_Version_Differs()
    {
        var (report, revision) = Seed();
        report.QuestionnaireVersion = "2.0";

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.Contains(result.Errors, e => e.Code == "questionnaire-version-mismatch");
    }

    [Theory]
    [InlineData(AssessmentReportRevisionStatus.Draft)]
    [InlineData(AssessmentReportRevisionStatus.InReview)]
    [InlineData(AssessmentReportRevisionStatus.Approved)]
    [InlineData(AssessmentReportRevisionStatus.Rejected)]
    public void Builder_Rejects_Revisions_That_Were_Never_Issued(AssessmentReportRevisionStatus status)
    {
        var (report, revision) = Seed();
        revision.Status = status;

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.Contains(result.Errors, e => e.Code == "revision-not-issued");
    }

    [Fact]
    public void Builder_Accepts_Superseded_Revision_For_Historical_Rendering()
    {
        var (report, revision) = Seed();
        revision.Status = AssessmentReportRevisionStatus.Superseded;

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Builder_Fails_When_Revision_Belongs_To_Another_Report()
    {
        var (report, revision) = Seed();
        revision.AssessmentReportId = Guid.NewGuid();

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.Contains(result.Errors, e => e.Code == "revision-report-mismatch");
    }

    // ---------------------------------------------------- readiness cross-check

    [Fact]
    public void Builder_Fails_When_Overall_Score_Disagrees_With_Snapshot()
    {
        var snapshot = Snapshot();
        var content = Content(snapshot);
        content = content with
        {
            ExecutiveSummary = content.ExecutiveSummary with
            {
                Readiness = content.ExecutiveSummary.Readiness! with { OverallDisplayScore0To100 = 99 }
            }
        };
        var (report, revision) = Seed(snapshot, content);

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "readiness-mismatch" && e.Message.Contains("overall display score"));
    }

    [Fact]
    public void Builder_Fails_When_Dimension_Score_Disagrees_With_Snapshot()
    {
        var snapshot = Snapshot();
        var content = Content(snapshot);
        var readiness = content.ExecutiveSummary.Readiness!;
        var dimensions = readiness.Dimensions.ToArray();
        dimensions[0] = dimensions[0] with { DisplayScore0To100 = 10 };
        content = content with
        {
            ExecutiveSummary = content.ExecutiveSummary with { Readiness = readiness with { Dimensions = dimensions } }
        };
        var (report, revision) = Seed(snapshot, content);

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.Contains(result.Errors, e => e.Code == "readiness-mismatch" && e.Message.Contains("'data'"));
    }

    [Fact]
    public void Builder_Fails_When_Content_Presents_A_Score_For_A_Withheld_Snapshot()
    {
        var withheld = Snapshot(withheld: true);
        var content = Content(Snapshot());
        var (report, revision) = Seed(withheld, content);

        var result = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "readiness-mismatch");
    }

    [Fact]
    public void Builder_Accepts_Content_Without_Readiness_Copy()
    {
        var snapshot = Snapshot();
        var content = Content(snapshot);
        content = content with { ExecutiveSummary = content.ExecutiveSummary with { Readiness = null } };
        var (report, revision) = Seed(snapshot, content);

        Assert.True(new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S")).Succeeded);
    }

    // ------------------------------------------------------ frozen-source guarantees

    [Fact]
    public void Rendering_Types_Have_No_Database_Scoring_Or_Questionnaire_Dependencies()
    {
        var forbidden = new[] { "DbContext", "IReadinessScoringService", "QuestionnaireDefinitionResolver", "AssessmentResponse" };
        var types = typeof(ReportPdfModelBuilder).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(ReportPdfModelBuilder).Namespace);

        foreach (var type in types)
        {
            var referenced = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Select(f => f.FieldType))
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(p => p.PropertyType));

            Assert.DoesNotContain(referenced, t => forbidden.Any(name => t.Name.Contains(name, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Build_And_Render_Do_Not_Mutate_Source_Entities()
    {
        var (report, revision) = Seed();
        var before = JsonSerializer.Serialize(new { report.Id, report.QuestionnaireVersion, report.ResponseSubmittedUtc }) +
            JsonSerializer.Serialize(new
            {
                revision.Status, revision.ReportTemplateVersion, revision.ConsultantContentJson, revision.ScoringSnapshot,
                revision.PdfStorageKey, revision.PdfSha256, revision.PdfLengthBytes, revision.ConcurrencyStamp, revision.UpdatedUtc
            });

        var model = new ReportPdfModelBuilder(Registry).Build(new ReportPdfSource(report, revision, "T", "S")).Model!;
        Render(model);

        var after = JsonSerializer.Serialize(new { report.Id, report.QuestionnaireVersion, report.ResponseSubmittedUtc }) +
            JsonSerializer.Serialize(new
            {
                revision.Status, revision.ReportTemplateVersion, revision.ConsultantContentJson, revision.ScoringSnapshot,
                revision.PdfStorageKey, revision.PdfSha256, revision.PdfLengthBytes, revision.ConcurrencyStamp, revision.UpdatedUtc
            });
        Assert.Equal(before, after);
        Assert.Null(revision.PdfStorageKey);
    }
}
