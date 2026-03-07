using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models;
using SentinelMfgSuite.Web.Services;
using System.Diagnostics;
using System.Text;

namespace SentinelMfgSuite.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly ApplicationDbContext _db;
        private readonly IEmailSender _emailSender;
        private readonly UserManager<ApplicationUser> _users;
        private readonly IConfiguration _config;
        private readonly ILogger<HomeController> _logger;

        public HomeController(
            IWebHostEnvironment env,
            ApplicationDbContext db,
            IEmailSender emailSender,
            UserManager<ApplicationUser> users,
            IConfiguration config,
            ILogger<HomeController> logger)
        {
            _env = env;
            _db = db;
            _emailSender = emailSender;
            _users = users;
            _config = config;
            _logger = logger;
        }

        public IActionResult Index() => View();
        [HttpGet]
        [Route("core-suite")]
        public IActionResult CoreSuite() => View();

        public IActionResult Platform() => View();
        public IActionResult Pricing() => View();
        public IActionResult UseCases() => View();
        public IActionResult Resources() => View();
        public IActionResult About() => View();

        [HttpGet("solutions")]
        public IActionResult Solutions() => View("Platform");

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel
            {
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
            });
        }

        [Authorize]
        public async Task<IActionResult> App()
        {
            var user = await _users.GetUserAsync(User);
            if (user?.TenantId == null)
                return RedirectToAction("Index", "Subscribe");

            return View();
        }

        [HttpGet]
        public IActionResult LaunchList()
        {
            return View(new LaunchListForm());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LaunchList(LaunchListForm model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            try
            {
                var appDataPath = Path.Combine(_env.ContentRootPath, "App_Data");
                Directory.CreateDirectory(appDataPath);

                var filePath = Path.Combine(appDataPath, "launch-list-signups.csv");

                var line = string.Join(",",
                    EscapeCsv(DateTime.UtcNow.ToString("O")),
                    EscapeCsv(model.Name),
                    EscapeCsv(model.Email),
                    EscapeCsv(model.Company),
                    EscapeCsv(model.Notes)
                ) + Environment.NewLine;

                await System.IO.File.AppendAllTextAsync(filePath, line, Encoding.UTF8);

                ViewBag.SuccessMessage = "You're on the list. We'll be in touch.";
                ModelState.Clear();

                return View(new LaunchListForm());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing launch list signup");
                ModelState.AddModelError("", "An error occurred. Please try again later.");
                return View(model);
            }
        }

        private static string EscapeCsv(string? value)
        {
            value ??= string.Empty;
            var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n');
            if (value.Contains('"'))
            {
                value = value.Replace("\"", "\"\"");
            }
            return needsQuotes ? $"\"{value}\"" : value;
        }

        public IActionResult GoToApp()
        {
            var appBase = _config["AppUrls:Base"] ?? "https://localhost:7052";
            return Redirect($"{appBase}/Subscribe");
        }

        [HttpGet]
        public IActionResult Contact(string? topic)
        {
            var model = new CustomBuildForm
            {
                Topic = NormalizeTopic(topic)
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Contact(CustomBuildForm model)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState
                    .Where(x => x.Value?.Errors.Count > 0)
                    .Select(x => $"{x.Key}: {string.Join(", ", x.Value!.Errors.Select(e => e.ErrorMessage))}");

                _logger.LogWarning("Contact form invalid -> {Errors}", string.Join(" | ", errors));
                return View(model);
            }

            try
            {
                var lead = new WebLead
                {
                    CreatedUtc = DateTime.UtcNow,
                    Name = model.Name,
                    Email = model.Email,
                    Company = model.Company,
                    Role = model.Role,
                    Topic = NormalizeTopic(model.Topic),
                    Message = model.Notes,
                    SourcePage = model.SourcePage,
                    Referrer = model.Referrer,
                    UtmSource = model.UtmSource,
                    UtmMedium = model.UtmMedium,
                    UtmCampaign = model.UtmCampaign,
                    UtmContent = model.UtmContent,
                    UtmTerm = model.UtmTerm
                };

                // Save lead first
                _db.WebLeads.Add(lead);
                _logger.LogInformation("DB: {Db} @ {Server}", _db.Database.GetDbConnection().Database, _db.Database.GetDbConnection().DataSource);
                await _db.SaveChangesAsync();
                _logger.LogInformation("WebLead saved: {Email} - {Topic}", lead.Email, lead.Topic);

                // Determine notification recipient (configurable)
                var notifyRecipient = _config["Notification:Recipient"] ?? "support@sentinelaisystems.net";

                var body = $@"New website lead:

Name: {lead.Name}
Email: {lead.Email}
Company: {lead.Company}
Role: {lead.Role}
Topic: {lead.Topic}

Source: {lead.UtmSource}/{lead.UtmCampaign}
Page: {lead.SourcePage}
Referrer: {lead.Referrer}

Message:
{lead.Message}";

                _logger.LogInformation("Preparing notification email to {NotifyRecipient} for lead {LeadEmail}", notifyRecipient, lead.Email);

                // Send email but do not fail the request if email sending fails
                try
                {
                    await _emailSender.SendAsync(
                        notifyRecipient,
                        $"New website lead – {lead.Topic ?? "General"}",
                        body
                    );

                    _logger.LogInformation("Notification email successfully sent to {NotifyRecipient} for lead {LeadEmail}", notifyRecipient, lead.Email);
                }
                catch (Exception emailEx)
                {
                    _logger.LogWarning(emailEx, "Failed to send notification to {NotifyRecipient} for lead {LeadEmail}. Lead persisted.", notifyRecipient, lead.Email);
                    // continue - lead already saved
                }

                return RedirectToAction("ContactThanks");
            }
            catch (DbUpdateException dbEx)
            {
                var inner = dbEx.InnerException?.Message ?? dbEx.Message;
                _logger.LogError(dbEx, "DB error saving WebLead. Inner={Inner}", inner);

                // DEV ONLY: surface the real reason on the page
                ModelState.AddModelError("", inner);
                return View(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing contact form");
                ModelState.AddModelError("", ex.Message); // DEV ONLY
                return View(model);
            }
        }

        [HttpGet]
        public IActionResult ContactThanks() => View();

        public IActionResult AdvancedWaitlist()
        {
            return View("Contact", new CustomBuildForm
            {
                Topic = "AdvancedWaitlist"
            });
        }

        private static string NormalizeTopic(string? topic)
        {
            if (string.IsNullOrWhiteSpace(topic))
                return "CustomBuild";

            return topic.Trim().ToLowerInvariant() switch
            {
                "pilot-application" or "pilot" or "pilot-partner" => "PilotApplication",
                "launch-list" or "launchlist" => "LaunchList",
                "pilot-rollout" or "pilotrollout" => "PilotRollout",
                "site-conversation" or "siteconversation" => "SiteConversation",
                "enterprise-multi-site" or "enterprisemultisite" => "EnterpriseMultiSite",
                "pricing-conversation" or "pricingconversation" => "PricingConversation",
                "advanced-waitlist" or "advancedwaitlist" or "advanced" => "AdvancedWaitlist",
                _ => "CustomBuild"
            };
        }
    }
}
