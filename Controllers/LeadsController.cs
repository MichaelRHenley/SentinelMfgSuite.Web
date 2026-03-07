using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SentinelMfgSuite.Web.Data;

namespace SentinelMfgSuite.Web.Controllers
{
    // NOTE: no [Authorize] here anymore
    public class LeadsController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly IConfiguration _config;

        public LeadsController(ApplicationDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        // Small helper: check the owner token
        private bool IsOwner()
        {
            var expected = _config["Leads:OwnerToken"];
            if (string.IsNullOrWhiteSpace(expected))
            {
                // safety: if no token configured, never show leads
                return false;
            }

            // 1) Try query string ?key=...
            var supplied = HttpContext.Request.Query["key"].ToString();

            // 2) If not in query, check cookie so you don't have to keep adding ?key=...
            if (string.IsNullOrEmpty(supplied) &&
                HttpContext.Request.Cookies.TryGetValue("sentinel_leads_key", out var cookieVal))
            {
                supplied = cookieVal;
            }

            var ok = string.Equals(supplied, expected, StringComparison.Ordinal);

            // If correct, refresh cookie for convenience
            if (ok)
            {
                HttpContext.Response.Cookies.Append(
                    "sentinel_leads_key",
                    expected,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.Strict,
                        Expires = DateTimeOffset.UtcNow.AddDays(30)
                    });
            }

            return ok;
        }

        public async Task<IActionResult> Index()
        {
            if (!IsOwner())
                return NotFound(); // or Unauthorized(); but NotFound hides that it even exists

            var leads = await _db.WebLeads
                .OrderByDescending(l => l.CreatedUtc)
                .Take(200)
                .ToListAsync();

            return View(leads);
        }

        public async Task<IActionResult> Details(int id)
        {
            if (!IsOwner())
                return NotFound();

            var lead = await _db.WebLeads.FindAsync(id);
            if (lead == null) return NotFound();

            return View(lead);
        }
    }
}


