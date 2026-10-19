using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class InternalAssessmentReplaceInvitationPageTests
{
    [Fact]
    public async Task ReplaceInvitation_Page_Unauthenticated_Access_Is_Challenged()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var client = CreateClient(factory);

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReplaceInvitation_Page_Authenticated_Without_AssessmentAdmin_Is_Forbidden()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReplaceInvitation_Page_Get_Shows_Confirmation_For_Eligible_Invitation()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Industrial Data & AI Readiness",
                SiteName = "Plant North",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Invited,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = "customer@example.com",
                TokenHash = "seed-hash",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(2),
                DeliveryStatus = AssessmentInvitationDeliveryStatus.Failed,
                DeliveryAttemptCount = 1
            });

            return Task.CompletedTask;
        });

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Production page request returned {(int)response.StatusCode}: {html}");
        Assert.Contains("customer@example.com", html, StringComparison.Ordinal);
        Assert.Contains("Confirm and replace invitation", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceInvitation_Page_Get_Shows_Ineligibility_Guidance_For_Used_Invitation()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Industrial Data & AI Readiness",
                SiteName = "Plant North",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Invited,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = "customer@example.com",
                TokenHash = "seed-hash",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(2),
                DeliveryStatus = AssessmentInvitationDeliveryStatus.Accepted,
                DeliveryAttemptCount = 1,
                AccessCount = 1,
                LastAccessUtc = DateTime.UtcNow.AddMinutes(-10)
            });

            return Task.CompletedTask;
        });

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Confirm and replace invitation", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_Shows_Replace_Invitation_Action_Only_For_Eligible_Invitations()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory();
        var eligibleAssessmentId = Guid.NewGuid();
        var eligibleInvitationId = Guid.NewGuid();
        var ineligibleAssessmentId = Guid.NewGuid();
        var ineligibleInvitationId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = eligibleAssessmentId,
                Title = "Eligible Assessment",
                SiteName = "Site Eligible",
                ContactEmail = "eligible@example.com",
                Status = AssessmentStatus.Invited,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
            db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = eligibleInvitationId,
                AssessmentId = eligibleAssessmentId,
                Email = "eligible@example.com",
                TokenHash = "eligible-hash",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(2),
                DeliveryStatus = AssessmentInvitationDeliveryStatus.Pending
            });

            db.Set<Assessment>().Add(new Assessment
            {
                Id = ineligibleAssessmentId,
                Title = "Accepted Assessment",
                SiteName = "Site Accepted",
                ContactEmail = "accepted@example.com",
                Status = AssessmentStatus.Invited,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
            db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = ineligibleInvitationId,
                AssessmentId = ineligibleAssessmentId,
                Email = "accepted@example.com",
                TokenHash = "accepted-hash",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(2),
                DeliveryStatus = AssessmentInvitationDeliveryStatus.Accepted
            });

            return Task.CompletedTask;
        });

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync("/Internal/Assessments");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"/Internal/Assessments/{eligibleAssessmentId}/ReplaceInvitation/{eligibleInvitationId}", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/Internal/Assessments/{ineligibleAssessmentId}/ReplaceInvitation/{ineligibleInvitationId}", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceInvitation_Page_Does_Not_Leak_Token_Or_Hash_In_Html()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Industrial Data & AI Readiness",
                SiteName = "Plant North",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Invited,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = "customer@example.com",
                TokenHash = "super-secret-token-hash-value",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(2),
                DeliveryStatus = AssessmentInvitationDeliveryStatus.Pending
            });

            return Task.CompletedTask;
        });

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("super-secret-token-hash-value", html, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceInvitation_Post_Unauthenticated_Access_Is_Challenged()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var client = CreateClient(factory);

        var response = await client.PostAsync(
            $"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}",
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReplaceInvitation_Post_Authenticated_Without_AssessmentAdmin_Is_Forbidden()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");

        var response = await client.PostAsync(
            $"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}",
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReplaceInvitation_Post_Development_Shows_TransientUrl_NoStore_Revokes_Old_Invitation_And_Does_Not_Persist_Raw_Token()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory("Development");
        var (assessmentId, invitationId) = await SeedEligibleInvitationAsync(factory);
        var client = CreateAdminClient(factory);
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client, assessmentId, invitationId);

        var response = await client.PostAsync(
            $"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = antiforgeryToken
            }));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/assessment/questionnaire/", html, StringComparison.OrdinalIgnoreCase);

        var linkMatch = Regex.Match(
            html,
            "href=\"(?<url>https?://[^\"]+/assessment/questionnaire/[^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(linkMatch.Success);

        var invitationUrl = WebUtility.HtmlDecode(linkMatch.Groups["url"].Value);
        Assert.Contains("https://localhost:7052/assessment/questionnaire/", invitationUrl, StringComparison.OrdinalIgnoreCase);
        var tokenMatch = Regex.Match(invitationUrl, "[?&]token=(?<token>[^&]+)", RegexOptions.IgnoreCase);
        Assert.True(tokenMatch.Success);
        var rawToken = Uri.UnescapeDataString(tokenMatch.Groups["token"].Value);
        Assert.False(string.IsNullOrWhiteSpace(rawToken));

        var oldInvitationResponse = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}");
        var oldInvitationHtml = await oldInvitationResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, oldInvitationResponse.StatusCode);
        Assert.DoesNotContain("Confirm and replace invitation", oldInvitationHtml, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var invitations = await db.Set<AssessmentInvitation>()
            .Where(i => i.AssessmentId == assessmentId)
            .ToListAsync();
        Assert.True(invitations.Count >= 2);
        Assert.NotNull(invitations.Single(i => i.Id == invitationId).RevokedUtc);
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
        Assert.Contains(invitations, i => i.TokenHash == expectedHash);
        Assert.DoesNotContain(invitations, i => i.TokenHash == rawToken);
        Assert.DoesNotContain(rawToken, string.Join(Environment.NewLine, factory.LogMessages), StringComparison.Ordinal);
        Assert.DoesNotContain(invitationUrl, string.Join(Environment.NewLine, factory.LogMessages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceInvitation_Post_IneligibleInvitation_DoesNotDisplayUrl()
    {
        await using var factory = new ReplaceInvitationWebApplicationFactory("Development");
        var (assessmentId, invitationId) = await SeedEligibleInvitationAsync(factory);
        var client = CreateAdminClient(factory);
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client, assessmentId, invitationId);
        await factory.SeedAsync(async db =>
        {
            var invitation = await db.Set<AssessmentInvitation>().SingleAsync(i => i.Id == invitationId);
            invitation.DeliveryStatus = AssessmentInvitationDeliveryStatus.Accepted;
        });

        var response = await client.PostAsync(
            $"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = antiforgeryToken
            }));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Open Test Questionnaire", html, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplaceInvitation_Post_Production_Suppresses_TransientUrl_And_Token_Output()
    {
        Assert.False(SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments.ReplaceInvitationModel
            .CanDisplayDevelopmentInvitationUrl(false, "https://localhost/assessment/questionnaire/id?token=transient"));
    }

    private static HttpClient CreateAdminClient(ReplaceInvitationWebApplicationFactory factory)
    {
        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");
        return client;
    }

    private static HttpClient CreateClient(ReplaceInvitationWebApplicationFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost:7052")
        });

    private static async Task<(Guid assessmentId, Guid invitationId)> SeedEligibleInvitationAsync(ReplaceInvitationWebApplicationFactory factory)
    {
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Industrial Data & AI Readiness",
                SiteName = "Plant North",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Invited,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = "customer@example.com",
                TokenHash = "seed-hash",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(2),
                DeliveryStatus = AssessmentInvitationDeliveryStatus.Pending
            });

            return Task.CompletedTask;
        });

        return (assessmentId, invitationId);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid assessmentId, Guid invitationId)
    {
        var getResponse = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReplaceInvitation/{invitationId}");
        var html = await getResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    private sealed class ReplaceInvitationWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"replace-invitation-{Guid.NewGuid():N}";
        private readonly SqliteConnection _connection;
        private readonly string _environment;
        private readonly ConcurrentQueue<string> _logMessages = new();

        public IEnumerable<string> LogMessages => _logMessages;

        public ReplaceInvitationWebApplicationFactory(string environment = "Development")
        {
            _environment = environment;
            _connection = new SqliteConnection($"Data Source={_databaseName};Mode=Memory;Cache=Shared");
            _connection.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environment);
            builder.UseSetting(WebHostDefaults.ServerUrlsKey, "https://localhost:7052");

            builder.UseSetting("https_port", "7052");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
                    ["AppUrls:Base"] = "https://localhost:7052"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(_logMessages));
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();

                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    options.UseSqlite(_connection);
                });

                services.RemoveAll<IEmailSender>();
                if (_environment == "Development")
                {
                    services.AddScoped<IEmailSender, DevelopmentSuppressingEmailSender>();
                }
                else
                {
                    services.AddScoped<IEmailSender, FakeSuppressedEmailSender>();
                }

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }

        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            private readonly ConcurrentQueue<string> _messages;

            public CapturingLoggerProvider(ConcurrentQueue<string> messages)
            {
                _messages = messages;
            }

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

            public void Dispose()
            {
            }
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _messages;

            public CapturingLogger(ConcurrentQueue<string> messages)
            {
                _messages = messages;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _messages.Enqueue(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }

        public async Task SeedAsync(Func<ApplicationDbContext, Task> seed)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            await seed(db);
            await db.SaveChangesAsync();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }

    }

    private sealed class FakeSuppressedEmailSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
            => Task.FromResult(EmailSendResult.Suppressed());
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
