using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;

namespace SentinelMfgSuite.Web.Controllers
{
    [Authorize(Policy = "SentinelAssessmentAdmin")]
    public class LeadsController : Controller
    {
        private readonly ApplicationDbContext _db;

        public LeadsController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {

            var leads = await _db.WebLeads
                .OrderByDescending(l => l.CreatedUtc)
                .Take(200)
                .ToListAsync();

            return View(leads);
        }

        public async Task<IActionResult> Details(int id)
        {

            var lead = await _db.WebLeads.FindAsync(id);
            if (lead == null) return NotFound();

            return View(lead);
        }
    }
}


