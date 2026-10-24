using System.Text.RegularExpressions;
using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;
using UglyToad.PdfPig;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class ReportTemplateV3PaginationTests
{
    private static readonly string[] IssuanceLabels =
    {
        "Reportidentifier", "Revisionidentifier", "Revisionnumber", "Reporttemplate", "Scoringcontractversion",
        "Scoringsnapshotschemaversion", "Consultantcontentschemaversion", "Issued", "Issuedby(userid)", "Approved", "Approvedby(userid)"
    };

    private static (int Pages, string Text) Inspect(byte[] pdf, string? saveAs = null)
    {
        if (saveAs is not null)
        {
            ValidationPdfOutput.Save(nameof(ReportTemplateV3PaginationTests), saveAs, pdf);
        }

        using var document = PdfDocument.Open(pdf);
        var text = Regex.Replace(string.Join(" ", document.GetPages().Select(p => p.Text)), @"\s+", string.Empty);
        return (document.NumberOfPages, text);
    }

    [Fact]
    public void Standard_V3_Report_Fits_Five_Pages_And_Keeps_All_Issuance_Fields()
    {
        var snapshot = ReportPdfRenderingTests.Snapshot();
        var model = ReportPdfRenderingTests.Model(snapshot, ReportPdfRenderingTests.Content(snapshot), ReportTemplateVersions.V3);
        var (pages, text) = Inspect(ReportPdfRenderingTests.Render(model), "v3-complete");

        Assert.True(pages <= 5, $"Expected at most 5 pages but rendered {pages}.");
        foreach (var label in IssuanceLabels)
        {
            Assert.Contains(label, text);
        }

        Assert.Contains(model.ReportId.ToString("D"), text);
        Assert.Contains(model.RevisionId.ToString("D"), text);
        Assert.Contains("sentinel-assessment-report/3", text);
    }

    [Fact]
    public void Corrected_V3_Report_Keeps_Correction_Fields_And_Page_Budget()
    {
        var snapshot = ReportPdfRenderingTests.Snapshot();
        var basedOn = Guid.NewGuid();
        var model = ReportPdfRenderingTests.Model(snapshot, ReportPdfRenderingTests.Content(snapshot), ReportTemplateVersions.V3, basedOn, "Corrected a recorded value.");
        var (pages, text) = Inspect(ReportPdfRenderingTests.Render(model), "v3-corrected-revision");

        Assert.True(pages <= 5, $"Rendered {pages} pages.");
        Assert.Contains("CorrectionDetails", text);
        Assert.Contains("Correctedrevisionnumber", text);
        Assert.Contains("Supersedesrevision", text);
        Assert.Contains("Correctsrevision", text);
        Assert.Contains(basedOn.ToString("D"), text);
        Assert.Contains("Correctionreason:Correctedarecordedvalue.", text);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, "Correctionreason").Count);
        Assert.Contains("(corrected)", text);
    }
}
