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
        public IActionResult CoreSuite() => RedirectToAction("Solutions");

        public IActionResult Platform() => View();
        public IActionResult Pricing() => RedirectToAction("Contact");
        public IActionResult UseCases() => View();
        public IActionResult Resources() => View();
        public IActionResult About() => View();

        [HttpGet("solutions")]
        public IActionResult Solutions() => View("Solutions");

        [HttpGet("services/industrial-ai-readiness")]
        public IActionResult IndustrialAIReadiness()
        {
            return View();
        }

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
            // Legacy launch list page should not serve public content. Redirect to contact for inquiries.
            return RedirectToAction("Contact");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(SentinelMfgSuite.Web.Services.Security.PublicRateLimiting.AnonymousPostPolicy)]
        public async Task<IActionResult> LaunchList(LaunchListForm model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }
            try
            {
                // Legacy endpoint retained for compatibility. Do not perform file system writes from the web app.
                _logger.LogInformation("LaunchList signup received (legacy endpoint) for {Email}", model?.Email);

                // Provide a friendly response while avoiding persistence in the legacy path.
                ViewBag.SuccessMessage = "Thanks — we'll be in touch.";
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
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(SentinelMfgSuite.Web.Services.Security.PublicRateLimiting.ContactPolicy)]
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
                    // Do NOT expose email exception details to the user; log for diagnostics only.
                    _logger.LogWarning(emailEx, "Failed to send notification to {NotifyRecipient} for lead {LeadEmail}. Lead persisted.", notifyRecipient, lead.Email);
                    // continue - lead already saved
                }

                TempData["LeadSubmitted"] = true;
                return RedirectToAction("ContactThanks");
            }
            catch (DbUpdateException dbEx)
            {
                // Log internal DB details for diagnostics, but do not reveal to end users.
                _logger.LogError(dbEx, "DB error saving WebLead.");
                ModelState.AddModelError("", "An internal error occurred while processing your request. Please try again later.");
                return View(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing contact form");
                ModelState.AddModelError("", "An unexpected error occurred. Please try again later.");
                return View(model);
            }
        }

        [HttpGet]
        public IActionResult ContactThanks()
        {
            ViewBag.TrackLead = TempData["LeadSubmitted"] is bool submitted && submitted;
            return View();
        }

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
                return "GeneralInquiry";

            return topic.Trim().ToLowerInvariant() switch
            {
                "industrial-ai-readiness" or "assess" => "IndustrialAIReadiness",
                "industrial-data-integration" or "connect" => "IndustrialDataIntegration",
                "applied-industrial-intelligence" or "solve" => "AppliedIndustrialIntelligence",
                "architecture-integration-support" or "scale" => "ArchitectureIntegrationSupport",
                "technology-partnership" or "technology" => "TechnologyPartnership",
                "general" or "general-inquiry" or "inquiry" => "GeneralInquiry",
                _ => "GeneralInquiry"
            };
        }
    }
}
