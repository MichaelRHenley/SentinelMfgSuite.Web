using System.Security.Cryptography;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Xunit;
using Xunit.Abstractions;
using Rendering = SentinelMfgSuite.Web.Tests.ReportPdfRenderingTests;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Geometry-based layout checks on rendered sample PDFs (word bounds, page-edge clipping, heading placement).
/// Set SENTINEL_PDF_SAMPLE_DIR to also write the sample PDFs to disk.
/// </summary>
public sealed class ReportPdfLayoutTests
{
    private readonly ITestOutputHelper _output;

    public ReportPdfLayoutTests(ITestOutputHelper output) => _output = output;

    private static string Long(string seed, int sentences)
        => string.Join(" ", Enumerable.Range(1, sentences).Select(i =>
            $"{seed} sentence {i} describes a recorded observation about line data, historian coverage and integration constraints at the site."));

    private static ConsultantReportContent LongContent(Services.Assessments.Scoring.ReadinessScoringSnapshot snapshot)
    {
        var evidence = new[]
        {
            new ReportEvidenceReference(ReportEvidenceKind.ScoringItem, "q-historian:historian", "q-historian", null, "historian"),
            new ReportEvidenceReference(ReportEvidenceKind.ScoringDimension, "data"),
            new ReportEvidenceReference(ReportEvidenceKind.ScoringReviewFlag, "FLAG-1")
        };

        var findings = Enumerable.Range(1, 12).Select(i => new ReportFinding(
            $"f{i}", $"Finding {i}: {Long("Title", 1)}", Long($"Finding {i}", 8),
            i % 3 == 0 ? ReportFindingClassification.UnsupportedClaim : ReportFindingClassification.Finding,
            ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.SelfReported, evidence)).ToArray();

        return Rendering.Content(snapshot) with
        {
            ExecutiveSummary = new ExecutiveSummarySection(
                ReportAssessmentState.Assessed, Long("Summary", 25), findings, null, Rendering.Readiness(snapshot)),
            TechnicalGapsAndArchitecture = new TechnicalGapsAndArchitectureSection(
                ReportAssessmentState.Assessed,
                Enumerable.Range(1, 8).Select(i => new TechnicalGapEntry($"g{i}", $"Gap {i}", Long("Gap", 6), Long("Impact", 3),
                    ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.SelfReported, evidence, i % 2 == 0)).ToArray(),
                Enumerable.Range(1, 8).Select(i => new ArchitectureRecommendation($"r{i}", $"Recommendation {i}", Long("Rec", 6), Long("Rationale", 4),
                    ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.ConsultantVerified, evidence)).ToArray()),
            ImplementationRoadmap = new ImplementationRoadmapSection(
                ReportAssessmentState.Assessed,
                Enumerable.Range(1, 10).Select(i => new RoadmapItem($"m{i}", $"Milestone {i}", Long("Activity", 4), i * 8 - 7, i * 8, i,
                    "Owner role", new[] { "Dependency A", "Dependency B" }, new[] { "Criterion one", "Criterion two" },
                    new[] { "Assumption one" }, "Measure", ReportContentProvenance.ConsultantEntered,
                    ReportVerificationStatus.SelfReported, evidence)).ToArray())
        };
    }

    private static (string Name, byte[] Pdf)[] Samples()
    {
        var snapshot = Rendering.Snapshot();
        var withheld = Rendering.Snapshot(withheld: true);
        var emptyContent = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.NotAssessed, null, null, null, Rendering.Readiness(snapshot))
        };
        return new[]
        {
            ("standard", Rendering.Render(Rendering.Model())),
            ("long-content", Rendering.Render(Rendering.Model(snapshot, LongContent(snapshot)))),
            ("withheld-scores", Rendering.Render(Rendering.Model(withheld, Rendering.Content(withheld)))),
            ("missing-optional", Rendering.Render(Rendering.Model(snapshot, emptyContent))),
            ("correction", Rendering.Render(Rendering.Model(basedOn: new Guid("44444444-4444-4444-4444-444444444444"), correctionReason: "Corrected a recorded figure.")))
        };
    }

    [Fact]
    public void Samples_Have_No_Clipping_Overflow_Or_Orphaned_Headings()
    {
        var dir = Environment.GetEnvironmentVariable("SENTINEL_PDF_SAMPLE_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        foreach (var (name, pdf) in Samples())
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                File.WriteAllBytes(Path.Combine(dir, name + ".pdf"), pdf);
            }

            using var document = PdfDocument.Open(pdf);
            _output.WriteLine($"{name}: {document.NumberOfPages} pages, {pdf.Length} bytes");

            foreach (var page in document.GetPages())
            {
                var words = page.GetWords().ToList();
                Assert.NotEmpty(words);

                foreach (var word in words)
                {
                    var box = word.BoundingBox;
                    Assert.True(box.Left >= 0 && box.Right <= page.Width && box.Bottom >= 0 && box.Top <= page.Height,
                        $"{name} p{page.Number}: word '{word.Text}' leaves the page.");
                    // Content must stay inside the side margins (50pt body, 60pt cover) with a small tolerance.
                    Assert.True(box.Left >= 45 && box.Right <= page.Width - 45,
                        $"{name} p{page.Number}: word '{word.Text}' is outside the margins ({box.Left:0.#}-{box.Right:0.#}).");
                }

                AssertFooterPresent(name, page, document.NumberOfPages);
                AssertNoOrphanedHeading(name, page, words);
            }
        }
    }

    private static void AssertFooterPresent(string name, Page page, int total)
    {
        if (page.Number == 1)
        {
            return;
        }

        var text = string.Concat(page.Text.Where(c => !char.IsWhiteSpace(c)));
        Assert.Contains($"Page{page.Number}of{total}", text);
    }

    private static void AssertNoOrphanedHeading(string name, Page page, List<Word> words)
    {
        if (page.Number == 1)
        {
            return;
        }

        // Body area excludes header (top) and footer (bottom). A numbered section heading must not be the
        // last content on a page: some body text has to sit below it.
        var bodyWords = words.Where(w => w.BoundingBox.Bottom > 60 && w.BoundingBox.Top < page.Height - 60).ToList();
        var lines = bodyWords.GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3) * 3).OrderByDescending(g => g.Key).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var lastLine = string.Join(" ", lines[^1].OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
        var isHeading = System.Text.RegularExpressions.Regex.IsMatch(lastLine, @"^\d{1,2}\. [A-Z]");
        Assert.False(isHeading, $"{name} p{page.Number}: orphaned heading '{lastLine}'.");
    }

    [Fact]
    public void Long_Content_Paginates_And_Is_Deterministic()
    {
        var snapshot = Rendering.Snapshot();
        var first = Rendering.Render(Rendering.Model(snapshot, LongContent(snapshot)));
        var second = Rendering.Render(Rendering.Model(snapshot, LongContent(snapshot)));

        using var document = PdfDocument.Open(first);
        Assert.True(document.NumberOfPages > 4, $"Expected multi-page output, got {document.NumberOfPages}.");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), Convert.ToHexString(SHA256.HashData(second)));
    }

    [Fact]
    public void Output_Does_Not_Embed_The_Current_Time()
    {
        var pdf = Rendering.Render(Rendering.Model());
        var raw = System.Text.Encoding.Latin1.GetString(pdf);

        Assert.DoesNotContain(DateTime.UtcNow.Year.ToString() + DateTime.UtcNow.Month.ToString("00") + DateTime.UtcNow.Day.ToString("00") + DateTime.UtcNow.Hour.ToString("00"), raw);
        using var document = PdfDocument.Open(pdf);
        Assert.Contains("20261020", document.Information.CreationDate);
    }

    [Fact]
    public void Hash_Fixture_Writes_Hashes_For_Cross_Process_Comparison()
    {
        var path = Environment.GetEnvironmentVariable("SENTINEL_PDF_HASH_OUT");
        var lines = Samples().Select(s => $"{s.Name}={Convert.ToHexString(SHA256.HashData(s.Pdf))}").ToArray();
        foreach (var line in lines) _output.WriteLine(line);
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllLines(path, lines);
        }
    }
}
