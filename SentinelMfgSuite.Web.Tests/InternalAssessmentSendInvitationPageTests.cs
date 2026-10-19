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

public sealed class InternalAssessmentSendInvitationPageTests
{
    [Fact]
    public async Task SendInvitation_Page_Unauthenticated_Access_Is_Challenged()
    {
        await using var factory = new SendInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/SendInvitation");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SendInvitation_Page_Authenticated_Without_AssessmentAdmin_Is_Forbidden()
    {
        await using var factory = new SendInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/SendInvitation");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SendInvitation_Page_Get_Shows_Confirmation_For_Draft_Assessment()
    {
        await using var factory = new SendInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Industrial Data & AI Readiness",
                SiteName = "Plant North",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Draft,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/SendInvitation");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("customer@example.com", html, StringComparison.Ordinal);
        Assert.Contains("Confirm and send invitation", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_Shows_Send_Invitation_Action_Only_For_Draft_Assessments()
    {
        await using var factory = new SendInvitationWebApplicationFactory();
        var draftId = Guid.NewGuid();
        var invitedId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = draftId,
                Title = "Draft Assessment",
                SiteName = "Site Draft",
                ContactEmail = "draft@example.com",
                Status = AssessmentStatus.Draft,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<Assessment>().Add(new Assessment
            {
                Id = invitedId,
                Title = "Invited Assessment",
                SiteName = "Site Invited",
                ContactEmail = "invited@example.com",
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
        Assert.Contains($"/Internal/Assessments/{draftId}/SendInvitation", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/Internal/Assessments/{invitedId}/SendInvitation", html, StringComparison.Ordinal);
    }

    private sealed class SendInvitationWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"send-invitation-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
                    ["AppUrls:Base"] = "https://sentinelaisystems.net"
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

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
