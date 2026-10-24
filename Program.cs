using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Provisioning;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

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

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SentinelAssessmentAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("AssessmentAdmin");
    });
});

// 4) Email
builder.Services.Configure<EmailSettings>(
    builder.Configuration.GetSection("Email"));

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddScoped<IEmailSender, DevelopmentSuppressingEmailSender>();
}
else
{
    builder.Services.AddScoped<IEmailSender, SesEmailSender>();
}


builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, TenantProvider>();
builder.Services.AddScoped<IAssessmentInvitationAccessService, AssessmentInvitationAccessService>();
builder.Services.AddScoped<IAssessmentQuestionnaireService, AssessmentQuestionnaireService>();
builder.Services.AddScoped<IAssessmentProvisioningService, AssessmentProvisioningService>();
builder.Services.AddScoped<IAssessmentInvitationIssuanceService, AssessmentInvitationIssuanceService>();
builder.Services.AddScoped<IAssessmentInvitationIssuanceTransactionParticipant, AssessmentInvitationIssuanceService>();
builder.Services.AddScoped<IAssessmentInvitationReplacementEligibilityService, AssessmentInvitationReplacementEligibilityService>();
builder.Services.AddScoped<IAssessmentInvitationDeliveryService, AssessmentInvitationDeliveryService>();
builder.Services.AddScoped<IAssessmentInvitationSendService, AssessmentInvitationSendService>();
builder.Services.AddScoped<IAssessmentInvitationReplacementSendService, AssessmentInvitationReplacementSendService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IAssessmentReportService, AssessmentReportService>();
builder.Services.AddSingleton<SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.IReportPdfTemplate, SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.SentinelAssessmentReportTemplateV1>();
builder.Services.AddSingleton<SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.IReportPdfTemplate, SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.SentinelAssessmentReportTemplateV2>();
builder.Services.AddSingleton<SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.IReportPdfTemplate, SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.SentinelAssessmentReportTemplateV3>();
builder.Services.AddSingleton<SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.IReportPdfTemplateRegistry, SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.ReportPdfTemplateRegistry>();
builder.Services.AddSingleton<SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.IReportPdfModelBuilder, SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.ReportPdfModelBuilder>();
builder.Services.AddSingleton<SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.IReportPdfRenderer, SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.ReportPdfRenderer>();
builder.Services.AddSingleton<SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.IReportPdfStorage>(sp =>
    new SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.FileSystemReportPdfStorage(
        SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.ReportPdfStorageConfiguration.ResolveRoot(
            sp.GetRequiredService<IConfiguration>()[SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.ReportPdfStorageConfiguration.RootPathSetting],
            sp.GetRequiredService<IHostEnvironment>())));
builder.Services.AddScoped<SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.IReportPdfFinalizationService, SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf.ReportPdfFinalizationService>();
builder.Services.AddScoped<IAssessmentReportRevisionContentService, AssessmentReportRevisionContentService>();
builder.Services.AddScoped<IAssessmentReportLifecycleService, AssessmentReportLifecycleService>();
builder.Services.AddSingleton<IAssessmentReportContentValidator, AssessmentReportContentValidator>();
builder.Services.AddSingleton<IReadinessScoringService, ReadinessScoringService>();

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
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto,
    KnownProxies = { IPAddress.Loopback, IPAddress.IPv6Loopback }
});
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
