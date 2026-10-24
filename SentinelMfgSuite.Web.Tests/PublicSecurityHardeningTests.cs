using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Data;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class PublicSecurityHardeningTests
{
    [Theory]
    [InlineData("/Leads")]
    [InlineData("/Leads/Index")]
    [InlineData("/Leads/Details/1")]
    public async Task Leads_AnonymousUser_IsDenied(string url)
    {
        await using var factory = new HardeningFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Leads_StaticOwnerTokenInQueryOrCookie_NoLongerGrantsAccess()
    {
        await using var factory = new HardeningFactory(("Leads:OwnerToken", "legacy-token"), ("OwnerToken", "legacy-token"));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var queryResponse = await client.GetAsync("/Leads?key=legacy-token");
        var request = new HttpRequestMessage(HttpMethod.Get, "/Leads");
        request.Headers.Add("Cookie", "sentinel_leads_key=legacy-token");
        var cookieResponse = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, queryResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, cookieResponse.StatusCode);
        Assert.False(cookieResponse.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Leads_AuthenticatedNonAdmin_IsForbidden()
    {
        await using var factory = new HardeningFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "SomeOtherRole");

        var response = await client.GetAsync("/Leads");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Leads_AssessmentAdmin_CanListLeads()
    {
        await using var factory = new HardeningFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "AssessmentAdmin");

        var response = await client.GetAsync("/Leads");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ContactPost_ExceedingLimit_Returns429WithRetryAfter()
    {
        await using var factory = new HardeningFactory(("RateLimiting:Contact:PermitLimit", "2"), ("RateLimiting:Contact:WindowSeconds", "600"));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await client.PostAsync("/Home/Contact", new FormUrlEncodedContent(new Dictionary<string, string>()));
            if (i < 2)
            {
                Assert.NotEqual(HttpStatusCode.TooManyRequests, last.StatusCode);
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.True(last.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task InvitationAccess_ExceedingLimit_Returns429_ButSaveTrafficIsSeparate()
    {
        await using var factory = new HardeningFactory(("RateLimiting:InvitationAccess:PermitLimit", "2"), ("RateLimiting:InvitationAccess:WindowSeconds", "600"));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var id = Guid.NewGuid();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await client.GetAsync($"/assessment/questionnaire/{id}?token=bad");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);

        var save = await client.PostAsync($"/assessment/questionnaire/{id}/draft", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, save.StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_FromUntrustedRemote_CannotSpoofClientIp()
    {
        await using var factory = new HardeningFactory(
            ("RateLimiting:InvitationAccess:PermitLimit", "2"),
            ("RateLimiting:InvitationAccess:WindowSeconds", "600"))
        {
            RemoteIp = IPAddress.Parse("203.0.113.9")
        };
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var id = Guid.NewGuid();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/assessment/questionnaire/{id}?token=bad");
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            last = await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_FromTrustedLoopbackProxy_SeparatesClients()
    {
        await using var factory = new HardeningFactory(
            ("RateLimiting:InvitationAccess:PermitLimit", "2"),
            ("RateLimiting:InvitationAccess:WindowSeconds", "600"))
        {
            RemoteIp = IPAddress.Loopback
        };
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var id = Guid.NewGuid();

        async Task<HttpStatusCode> Get(string forwardedFor)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/assessment/questionnaire/{id}?token=bad");
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Get("198.51.100.1"));
        Assert.Equal(HttpStatusCode.OK, await Get("198.51.100.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Get("198.51.100.1"));
        Assert.Equal(HttpStatusCode.OK, await Get("198.51.100.2"));
    }

    [Fact]
    public async Task ForwardedFor_FromTrustedProxy_UsesOnlyLastHop_NotClientSuppliedChain()
    {
        await using var factory = new HardeningFactory(
            ("RateLimiting:InvitationAccess:PermitLimit", "2"),
            ("RateLimiting:InvitationAccess:WindowSeconds", "600"))
        {
            RemoteIp = IPAddress.Loopback
        };
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var id = Guid.NewGuid();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/assessment/questionnaire/{id}?token=bad");
            // Proxy appends the real client (198.51.100.50); the leading entry is attacker-controlled.
            request.Headers.Add("X-Forwarded-For", $"192.0.2.{i + 1}, 198.51.100.50");
            last = await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    [Fact]
    public async Task LoginPost_ExceedingLimit_Returns429_ButLoginPageViewIsNotLimited()
    {
        await using var factory = new HardeningFactory(
            ("RateLimiting:Login:PermitLimit", "2"),
            ("RateLimiting:Login:WindowSeconds", "600"));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var i = 0; i < 5; i++)
        {
            var view = await client.GetAsync("/Identity/Account/Login");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, view.StatusCode);
        }

        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>()));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    [Fact]
    public async Task Routes_InProduction_AnonymousIsDenied()
    {
        await using var factory = new HardeningFactory { EnvironmentName = "Production" };
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/_routes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Routes_InProduction_NonAdminIsForbidden()
    {
        await using var factory = new HardeningFactory { EnvironmentName = "Production" };
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "SomeOtherRole");

        var response = await client.GetAsync("/_routes");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Routes_InProduction_AssessmentAdminIsAllowed()
    {
        await using var factory = new HardeningFactory { EnvironmentName = "Production" };
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "AssessmentAdmin");

        var response = await client.GetAsync("/_routes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Routes_InDevelopment_IsAvailable()
    {
        await using var factory = new HardeningFactory { EnvironmentName = "Development" };
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/_routes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Identity_Lockout_IsExplicitlyConfigured()
    {
        await using var factory = new HardeningFactory();
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<Microsoft.AspNetCore.Identity.IdentityOptions>>().Value;

        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Lockout.DefaultLockoutTimeSpan);
        Assert.True(options.Lockout.AllowedForNewUsers);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestHardening";
        public const string RolesHeader = "X-Test-Roles";

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RolesHeader, out var roles))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-user"), new(ClaimTypes.Name, "test-user") };
            claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }
    }

    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        private readonly Func<IPAddress?> _remoteIp;

        public RemoteIpStartupFilter(Func<IPAddress?> remoteIp)
        {
            _remoteIp = remoteIp;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                var ip = _remoteIp();
                if (ip is not null)
                {
                    context.Connection.RemoteIpAddress = ip;
                }

                return nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class HardeningFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"hardening-{Guid.NewGuid():N}";
        private readonly (string Key, string Value)[] _settings;

        public IPAddress? RemoteIp { get; set; }

        public string? EnvironmentName { get; set; }

        public HardeningFactory(params (string Key, string Value)[] settings)
        {
            _settings = settings;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (EnvironmentName is not null)
            {
                builder.UseEnvironment(EnvironmentName);
            }

            builder.UseSetting("AllowedHosts", "*");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=unused;Username=unused;Password=unused");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=unused;Password=unused"
                };
                foreach (var (key, value) in _settings)
                {
                    values[key] = value;
                }

                config.AddInMemoryCollection(values);
            });

            builder.ConfigureServices(services =>
            {
                services.AddTransient<IStartupFilter>(_ => new RemoteIpStartupFilter(() => RemoteIp));
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(_databaseName));

                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                });
            });
        }
    }
}
