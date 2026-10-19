using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class InternalReadinessReportPreviewTests
{
    [Fact]
    public async Task Sample_Report_Anonymous_Access_Is_Challenged()
    {
        await using var factory = new SampleReportWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Internal/AssessmentReport/Sample");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("tenantadmin")]
    public async Task Sample_Report_Non_Assessment_Admin_Is_Forbidden(string role)
    {
        await using var factory = new SampleReportWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, role);

        var response = await client.GetAsync("/Internal/AssessmentReport/Sample");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sample_Report_Assessment_Admin_Renders_Fictional_Content_Without_Reading_Assessments()
    {
        await using var factory = new SampleReportWebApplicationFactory();
        await factory.SeedUnrelatedAssessmentAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync("/Internal/AssessmentReport/Sample");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("SAMPLE / NOT FOR CUSTOMER DISTRIBUTION", html, StringComparison.Ordinal);
        Assert.Contains("Northstar Components Ltd. (Fictional)", html, StringComparison.Ordinal);
        Assert.Contains("Lakeview Manufacturing Centre (Fictional)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("REAL-CUSTOMER-CANARY", html, StringComparison.Ordinal);
        Assert.DoesNotContain("RESPONSE-CANARY", html, StringComparison.Ordinal);
        Assert.Contains("Print / Save as PDF", html, StringComparison.Ordinal);
        Assert.Contains("Industrial Data &amp; AI Readiness Assessment", html, StringComparison.Ordinal);
        Assert.Contains("Insufficient Evidence", html, StringComparison.Ordinal);
        Assert.Contains("V1:</strong> historical version; readiness scoring is unavailable.", html, StringComparison.Ordinal);
        Assert.Contains("V2:</strong> D1 is not scored and the overall score is unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("q15.integration_level", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/Internal/Assessments/", html, StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Illustrative_V3_Sample_Scores_Match_Documentation_Arithmetic()
    {
        var report = SampleReadinessReportViewModel.Create();
        var weightedRaw = report.Dimensions.Sum(dimension => double.Parse(dimension.RawScore) * dimension.WeightPercent / 100d);
        var roundedOverall = (int)Math.Round(weightedRaw, MidpointRounding.AwayFromZero);

        Assert.Equal(100, report.Dimensions.Sum(dimension => dimension.WeightPercent));
        Assert.Equal(66.875, weightedRaw, 3);
        Assert.Equal(67, roundedOverall);
        Assert.Equal(report.OverallScore, $"{roundedOverall} / 100");
        Assert.Equal("Established", report.OverallMaturity);
        Assert.Equal(new[] { 88, 63, 50 }, report.Dimensions.Select(dimension => dimension.Score));
        Assert.Equal(new[] { "Advanced", "Established", "Established" }, report.Dimensions.Select(dimension => dimension.Maturity));
        Assert.Contains("No numeric score or maturity classification is assigned", report.InsufficientEvidenceExamples[2], StringComparison.Ordinal);
    }

    private sealed class SampleReportWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"sample-report-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=unused;Password=unused"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(_databaseName));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }

        public async Task SeedUnrelatedAssessmentAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var assessmentId = Guid.NewGuid();
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "REAL-CUSTOMER-CANARY",
                SiteName = "Hidden site",
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = Guid.NewGuid(),
                Data = "{\"response\":\"RESPONSE-CANARY\"}",
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "SampleReportTestAuth";
        public const string HeaderName = "X-Test-Auth";

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var values))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "sample-report-test-user"),
                new(ClaimTypes.Name, "sample-report-test-user")
            };
            var role = values.ToString().ToLowerInvariant() switch
            {
                "assessmentadmin" => "AssessmentAdmin",
                "tenantadmin" => "TenantAdmin",
                _ => "User"
            };
            claims.Add(new Claim(ClaimTypes.Role, role));

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
}