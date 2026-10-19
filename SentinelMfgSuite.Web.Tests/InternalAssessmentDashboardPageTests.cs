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
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class InternalAssessmentDashboardPageTests
{
    [Fact]
    public async Task Internal_Assessment_Dashboard_Unauthenticated_Access_Is_Challenged()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Internal/Assessments");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Internal_Assessment_Dashboard_Authenticated_Without_AssessmentAdmin_Is_Forbidden()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");

        var response = await client.GetAsync("/Internal/Assessments");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Internal_Assessment_Create_Page_Unauthenticated_Access_Is_Challenged()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Internal/Assessments/Create");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Internal_Assessment_Create_Page_Authenticated_Without_AssessmentAdmin_Is_Forbidden()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");

        var response = await client.GetAsync("/Internal/Assessments/Create");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Internal_Assessment_Dashboard_TenantAdmin_Alone_Is_Forbidden()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "tenantadmin");

        var response = await client.GetAsync("/Internal/Assessments");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Navigation_Shows_Assessments_Link_For_AssessmentAdmin()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/Internal/Assessments\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Internal_Assessment_Dashboard_Shows_Create_Assessment_Button_For_AssessmentAdmin()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync("/Internal/Assessments");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/Internal/Assessments/Create\"", html, StringComparison.Ordinal);
        Assert.Contains("Create Assessment", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Internal_Assessment_Dashboard_Shows_Report_Entry_Only_For_Submitted_Response()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        var submittedId = Guid.NewGuid();
        var inProgressId = Guid.NewGuid();
        await factory.SeedAsync(db =>
        {
            var now = DateTime.UtcNow;
            var submitted = new Assessment { Id = submittedId, Status = AssessmentStatus.Submitted, CreatedUtc = now, UpdatedUtc = now };
            var inProgress = new Assessment { Id = inProgressId, Status = AssessmentStatus.InProgress, CreatedUtc = now, UpdatedUtc = now };
            db.AddRange(submitted, inProgress);
            db.AddRange(
                new AssessmentResponse { Id = Guid.NewGuid(), AssessmentId = submittedId, InvitationId = Guid.NewGuid(), Data = "{}", IsDraft = false, LastSavedUtc = now, SubmittedUtc = now, ConcurrencyStamp = "submitted" },
                new AssessmentResponse { Id = Guid.NewGuid(), AssessmentId = inProgressId, InvitationId = Guid.NewGuid(), Data = "{}", IsDraft = true, LastSavedUtc = now, ConcurrencyStamp = "draft" });
            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");
        var response = await client.GetAsync("/Internal/Assessments");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"/Internal/Assessments/Report/{submittedId}", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/Internal/Assessments/Report/{inProgressId}", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Navigation_Hides_Assessments_Link_For_Anonymous_And_Non_Admin_Users()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();

        var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anonymousResponse = await anonymousClient.GetAsync("/");
        var anonymousHtml = await anonymousResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, anonymousResponse.StatusCode);
        Assert.DoesNotContain("href=\"/Internal/Assessments\"", anonymousHtml, StringComparison.Ordinal);

        var tenantAdminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        tenantAdminClient.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "tenantadmin");

        var tenantAdminResponse = await tenantAdminClient.GetAsync("/");
        var tenantAdminHtml = await tenantAdminResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, tenantAdminResponse.StatusCode);
        Assert.DoesNotContain("href=\"/Internal/Assessments\"", tenantAdminHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Internal_Assessment_Dashboard_AssessmentAdmin_Is_Authorized()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = Guid.NewGuid(),
                Title = "Industrial Data & AI Readiness",
                SiteName = "Plant North",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Invited,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync("/Internal/Assessments");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sentinel AI Systems", html, StringComparison.Ordinal);
        Assert.Contains("Assessment Administration", html, StringComparison.Ordinal);
        Assert.Contains("Industrial Data &amp; AI Readiness", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Internal_Assessment_Dashboard_Projection_Excludes_Invitation_Secrets()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();

        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Assessment A",
                SiteName = "Site A",
                ContactEmail = "a@example.com",
                Status = AssessmentStatus.Invited,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = "a@example.com",
                TokenHash = "token-hash-should-never-render",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddDays(2),
                RevokedUtc = DateTime.UtcNow,
                DeliveryStatus = AssessmentInvitationDeliveryStatus.Failed,
                DeliveryFailureCode = "EmailProviderFailure"
            });

            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = invitationId,
                Data = "{\"secret\":\"response-json-should-never-render\"}",
                IsDraft = true,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = null,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });

            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync("/Internal/Assessments");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Revoked", html, StringComparison.Ordinal);
        Assert.Contains("Failed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("token-hash-should-never-render", html, StringComparison.Ordinal);
        Assert.DoesNotContain("response-json-should-never-render", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/assessment/questionnaire/", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Internal_Assessment_Dashboard_Shows_Empty_State_When_No_Assessments_Exist()
    {
        await using var factory = new InternalDashboardWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync("/Internal/Assessments");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No assessments have been created yet.", html, StringComparison.Ordinal);
    }

    private sealed class InternalDashboardWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"internal-dashboard-{Guid.NewGuid():N}";

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

                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    options.UseInMemoryDatabase(_databaseName);
                });

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }

        public async Task SeedAsync(Func<ApplicationDbContext, Task> seed)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await seed(db);
            await db.SaveChangesAsync();
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestAuth";
        public const string HeaderName = "X-Test-Auth";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var values))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var mode = values.ToString();
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "test-user"),
                new(ClaimTypes.Name, "test-user")
            };

            if (string.Equals(mode, "assessmentadmin", StringComparison.OrdinalIgnoreCase))
            {
                claims.Add(new Claim(ClaimTypes.Role, "AssessmentAdmin"));
            }
            else if (string.Equals(mode, "tenantadmin", StringComparison.OrdinalIgnoreCase))
            {
                claims.Add(new Claim(ClaimTypes.Role, "TenantAdmin"));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
