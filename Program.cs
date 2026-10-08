using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Provisioning;

var builder = WebApplication.CreateBuilder(args);

Console.WriteLine("🚀 SentinelMfgSuite.Web booted @ " + DateTime.UtcNow.ToString("O"));

var cs = builder.Configuration.GetConnectionString("DefaultConnection");
Console.WriteLine("DefaultConnection configured: " + (string.IsNullOrEmpty(cs) ? "NOT SET" : "SET"));

// Connection strings
// ✅ One DB only (Azure SQL SentinelMfgSuite_Identity)
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
Console.WriteLine("DefaultConnection configured: " + (string.IsNullOrEmpty(connStr) ? "NOT SET" : "SET"));

if (string.IsNullOrEmpty(connStr))
    throw new InvalidOperationException("DefaultConnection is not configured!");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    // Use PostgreSQL via Npgsql. Connection string is read from Configuration: ConnectionStrings:DefaultConnection
    // For local development, supply the connection string via User Secrets or environment variable.
    options.UseNpgsql(connStr));

// 2) Add Identity + default UI
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false; // fine for now
})
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

// 3) MVC
builder.Services.AddControllersWithViews();

// 4) Email
builder.Services.Configure<EmailSettings>(
    builder.Configuration.GetSection("Email"));

builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();


builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, TenantProvider>();

builder.Services.AddScoped<
    IUserClaimsPrincipalFactory<ApplicationUser>,
    SentinelMfgSuite.Web.Services.Auth.AppUserClaimsPrincipalFactory>();

builder.Services.AddScoped<SentinelMfgSuite.Web.Services.Provisioning.RequireTenantFilter>();

builder.Services.AddLogging(config =>
{
    config.AddConsole();
    config.AddDebug();
    config.SetMinimumLevel(LogLevel.Information);
});

var app = builder.Build();




// PIPELINE
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// 🔐 Important: auth BEFORE authorization
app.UseAuthentication();
app.UseAuthorization();

// MVC routes
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Identity UI (Razor Pages)
app.MapRazorPages();

app.MapGet("/_routes", (IEnumerable<EndpointDataSource> sources) =>
{
    var endpoints = sources.SelectMany(s => s.Endpoints)
        .Select(e => e.DisplayName)
        .Where(n => n != null)
        .OrderBy(n => n)
        .ToArray();

    return Results.Json(endpoints);
});

app.Run();
