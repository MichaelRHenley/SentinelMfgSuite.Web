using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SentinelMfgSuite.Web.Areas.Identity.Pages.Account
{
    // This page intentionally blocks the default Identity registration endpoints.
    // Both GET and POST return 404 to prevent public self-registration.
    public class RegisterModel : PageModel
    {
        public IActionResult OnGet()
        {
            return NotFound();
        }

        public IActionResult OnPost()
        {
            return NotFound();
        }
    }
}
