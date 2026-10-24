using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;
using UglyToad.PdfPig;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class ReportTemplateV3Tests
{
    private static readonly ReportPdfTemplateRegistry Registry = new();

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    [Fact]
    public void Brand_Assets_Load_As_Small_Png_Resources()
    {
        foreach (var asset in new[] { ReportBrandAssets.LogoWhite, ReportBrandAssets.LogoDark, ReportBrandAssets.EmblemDark })
        {
            Assert.True(IsPng(asset));
            Assert.InRange(asset.Length, 1, 300_000);
        }
    }

    [Fact]
    public void V3_Is_Registered_And_Is_The_Current_Template()
    {
        Assert.Equal("sentinel-assessment-report/3", ReportTemplateVersions.V3);
        Assert.Equal(ReportTemplateVersions.V3, ReportTemplateVersions.Current);
        Assert.Equal(ReportTemplateVersions.V3, Registry.Resolve(ReportTemplateVersions.V3).ResolvedVersion);
        Assert.Equal(ReportTemplateVersions.V1, Registry.Resolve(ReportTemplateVersions.V1).ResolvedVersion);
        Assert.Equal(ReportTemplateVersions.V2, Registry.Resolve(ReportTemplateVersions.V2).ResolvedVersion);
        Assert.False(Registry.Resolve("sentinel-assessment-report/4").IsSupported);
		Assert.False(Registry.Resolve("sentinel-assessment-report/unknown").IsSupported);
		Assert.Equal(ReportTemplateVersions.V1, Registry.Resolve(null).ResolvedVersion);
		Assert.True(Registry.Resolve(null).IsLegacyNullCompatibility);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V3_Renders_Branded_Pdf(bool corrected)
    {
        var snapshot = ReportPdfRenderingTests.Snapshot();
        var model = ReportPdfRenderingTests.Model(snapshot, ReportPdfRenderingTests.Content(snapshot), ReportTemplateVersions.V3,
            corrected ? Guid.NewGuid() : null, corrected ? "Corrected a recorded value." : null);
        var pdf = ReportPdfRenderingTests.Render(model);

        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        ValidationPdfOutput.Save(nameof(ReportTemplateV3Tests), corrected ? "v3-corrected" : "v3-sample", pdf);

        using var document = PdfDocument.Open(pdf);
        Assert.True(document.GetPage(1).GetImages().Any());
        var text = string.Concat(document.GetPages().SelectMany(p => p.Text).Where(c => !char.IsWhiteSpace(c)));
        Assert.Contains("Confidential", text);
        Assert.Contains("SpringfieldPlant", text);
    }
}
