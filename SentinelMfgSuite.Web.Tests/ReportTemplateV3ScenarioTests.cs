using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class ReportTemplateV3ScenarioTests
{
    [Fact]
    public void V3_Generates_Review_Scenarios()
    {
        var snap = ReportPdfRenderingTests.Snapshot();
        var withheld = ReportPdfRenderingTests.Snapshot(true);
        var content = ReportPdfRenderingTests.Content(snap);
        var longText = string.Join(" ", Enumerable.Repeat("The site runs several legacy PLC networks with limited historian coverage and manual data re-keying across shifts.", 25));
        var baseItem = content.ImplementationRoadmap.Items![0];
        var items = Enumerable.Range(1, 7).Select(i => baseItem with
        {
            Id = "x" + i,
            Title = "Roadmap action " + i,
            Activities = longText,
            Priority = i,
            StartDay = i == 5 ? -1 : i * 10,
            EndDay = i == 5 ? -1 : i * 10 + 45
        }).ToArray();
        var longContent = content with
        {
            ExecutiveSummary = content.ExecutiveSummary with { Narrative = longText },
            ImplementationRoadmap = content.ImplementationRoadmap with { Items = items }
        };

        var cases = new (string Name, ReportPdfRenderModel Model)[]
        {
            ("v3-complete", ReportPdfRenderingTests.Model(snap, content, ReportTemplateVersions.V3)),
            ("v3-withheld", ReportPdfRenderingTests.Model(withheld, ReportPdfRenderingTests.Content(withheld), ReportTemplateVersions.V3)),
            ("v3-incomplete", ReportPdfRenderingTests.Model(snap, ConsultantReportContent.CreateEmpty() with { ExecutiveSummary = content.ExecutiveSummary }, ReportTemplateVersions.V3)),
            ("v3-corrected-revision", ReportPdfRenderingTests.Model(snap, content, ReportTemplateVersions.V3, Guid.NewGuid(), "Corrected a recorded value.")),
            ("v3-long-roadmap", ReportPdfRenderingTests.Model(snap, longContent, ReportTemplateVersions.V3))
        };
        foreach (var (name, model) in cases)
        {
            var pdf = ReportPdfRenderingTests.Render(model);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
            ValidationPdfOutput.Save(nameof(ReportTemplateV3ScenarioTests), name, pdf);
        }
    }
}

