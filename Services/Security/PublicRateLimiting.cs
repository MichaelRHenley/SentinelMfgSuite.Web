using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SentinelMfgSuite.Web.Services.Security;

public static class PublicRateLimiting
{
    public const string ContactPolicy = "public-contact";
    public const string InvitationAccessPolicy = "invitation-access";
    public const string QuestionnaireSavePolicy = "questionnaire-save";
    public const string AnonymousPostPolicy = "anonymous-post";
    public const string LoginPolicy = "login";

    private const string SectionName = "RateLimiting";

    public static IServiceCollection AddPublicRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            var section = configuration.GetSection(SectionName);

            var contact = Read(section.GetSection("Contact"), permitLimit: 5, windowSeconds: 600);
            var access = Read(section.GetSection("InvitationAccess"), permitLimit: 30, windowSeconds: 60);
            var save = Read(section.GetSection("QuestionnaireSave"), permitLimit: 120, windowSeconds: 60);
            var anon = Read(section.GetSection("AnonymousPost"), permitLimit: 20, windowSeconds: 60);
            var login = Read(section.GetSection("Login"), permitLimit: 10, windowSeconds: 60);

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.Headers["Cache-Control"] = "no-store";
                return ValueTask.CompletedTask;
            };

            AddPolicy(options, ContactPolicy, contact, ctx => ClientKey(ctx));
            AddPolicy(options, InvitationAccessPolicy, access, ctx => ClientKey(ctx));
            AddPolicy(options, AnonymousPostPolicy, anon, ctx => ClientKey(ctx));
            // Only credential submissions are counted; viewing the login page is not limited.
            options.AddPolicy(LoginPolicy, context => HttpMethods.IsPost(context.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = login.Permit,
                    Window = login.Window,
                    QueueLimit = 0,
                    AutoReplenishment = true
                })
                : RateLimitPartition.GetNoLimiter("login-non-post"));
            // Keyed by client and invitation so one invitation's autosave traffic does not throttle others.
            AddPolicy(options, QuestionnaireSavePolicy, save,
                ctx => $"{ClientKey(ctx)}|{ctx.Request.RouteValues["invitationId"]}");
        });

        return services;
    }

    // RemoteIpAddress is only rewritten by UseForwardedHeaders for configured trusted proxies,
    // so an arbitrary client-supplied X-Forwarded-For cannot choose its own bucket.
    private static string ClientKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static void AddPolicy(RateLimiterOptions options, string name, (int Permit, TimeSpan Window) limit, Func<HttpContext, string> key)
    {
        options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
            key(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit.Permit,
                Window = limit.Window,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    private static (int Permit, TimeSpan Window) Read(IConfigurationSection section, int permitLimit, int windowSeconds)
    {
        var permit = section.GetValue("PermitLimit", permitLimit);
        var window = section.GetValue("WindowSeconds", windowSeconds);
        return (Math.Max(1, permit), TimeSpan.FromSeconds(Math.Max(1, window)));
    }
}
