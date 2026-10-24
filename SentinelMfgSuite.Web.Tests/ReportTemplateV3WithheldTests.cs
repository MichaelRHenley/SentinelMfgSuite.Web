using System.Text.RegularExpressions;
using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;
using UglyToad.PdfPig;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class ReportTemplateV3WithheldTests
{
    private static string Squash(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return Regex.Replace(string.Join(" ", document.GetPages().Select(p => p.Text)), @"\s+", string.Empty);
    }

    [Fact]
    public void V3_Withheld_Report_Identifies_V3_And_Never_Shows_Withheld_Scores()
    {
        var snapshot = ReportPdfRenderingTests.Snapshot(withheld: true);
        var model = ReportPdfRenderingTests.Model(snapshot, ReportPdfRenderingTests.Content(snapshot), ReportTemplateVersions.V3);
        Assert.Equal(ReportTemplateVersions.V3, model.TemplateVersion);

        var pdf = ReportPdfRenderingTests.Render(model);
        var text = Squash(pdf);
        ValidationPdfOutput.Save(nameof(ReportTemplateV3WithheldTests), "v3-withheld-explicit", pdf);

        Assert.Contains("sentinel-assessment-report/3", text);
        Assert.DoesNotContain("sentinel-assessment-report/2", text);
        Assert.Contains("Notscored", text);
        Assert.DoesNotContain("outof100", text);
        Assert.DoesNotContain("63/100", text);
        Assert.DoesNotContain("54/100", text);
        Assert.DoesNotMatch(@"(?<!\d)0/100", text);
        Assert.Contains("Notscored(withheld)", text);
        Assert.Contains("71/100", text);
        Assert.Contains("overallrequiresalldimensions", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("insufficientscoreableitems", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V3_Eligible_Report_Shows_Overall_And_Dimension_Scores_Once_Each()
    {
        var snapshot = ReportPdfRenderingTests.Snapshot();
        var model = ReportPdfRenderingTests.Model(snapshot, ReportPdfRenderingTests.Content(snapshot), ReportTemplateVersions.V3);
        var text = Squash(ReportPdfRenderingTests.Render(model));

        Assert.Contains("outof100", text);
        Assert.Contains("54/100", text);
        Assert.DoesNotContain("Dimensionsummary", text);
        Assert.Equal(1, Regex.Matches(text, "71/100").Count - Regex.Matches(text, "score71/100").Count);
    }
}
