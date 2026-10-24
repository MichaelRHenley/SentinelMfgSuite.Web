using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Uses the application's real service registrations (only the database is replaced) so a missing
/// PDF template registration in Program.cs is detected.
/// </summary>
public sealed class ReportPdfProductionRegistrationTests
{
    private sealed class ProductionDiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=unused;Password=unused"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase($"pdf-di-{Guid.NewGuid():N}"));
            });
        }
    }

    [Fact]
    public void Production_Container_Registers_Current_Template()
    {
        using var factory = new ProductionDiFactory();
        var templates = factory.Services.GetServices<IReportPdfTemplate>().ToList();
        Assert.Contains(templates, t => t.Version == ReportTemplateVersions.Current);
        Assert.Contains(templates, t => t is SentinelAssessmentReportTemplateV1);
    }

    [Fact]
    public void Production_Registry_Resolves_V1_And_Rejects_Unknown_Versions()
    {
        using var factory = new ProductionDiFactory();
        var registry = factory.Services.GetRequiredService<IReportPdfTemplateRegistry>();

        var v1 = registry.Resolve("sentinel-assessment-report/1");
        Assert.True(v1.IsSupported);
        Assert.Equal("sentinel-assessment-report/1", v1.ResolvedVersion);

        var legacy = registry.Resolve(null);
        Assert.True(legacy.IsSupported);
        Assert.True(legacy.IsLegacyNullCompatibility);

        Assert.False(registry.Resolve("sentinel-assessment-report/999").IsSupported);
    }

    [Fact]
    public void Production_Renderer_Produces_Nonempty_Pdf_For_Valid_Model()
    {
        using var factory = new ProductionDiFactory();
        var renderer = factory.Services.GetRequiredService<IReportPdfRenderer>();

        var pdf = renderer.Render(ReportPdfRenderingTests.Model());

        Assert.NotEmpty(pdf);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Fact]
    public void Production_Renderer_Rejects_Unknown_Template_Version()
    {
        using var factory = new ProductionDiFactory();
        var renderer = factory.Services.GetRequiredService<IReportPdfRenderer>();
        var model = ReportPdfRenderingTests.Model() with { TemplateVersion = "sentinel-assessment-report/999" };

        Assert.Throws<ReportPdfTemplateUnsupportedException>(() => renderer.Render(model));
    }
}
