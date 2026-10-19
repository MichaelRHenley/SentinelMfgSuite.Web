using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SentinelMfgSuite.Web.Models.Assessments.Reports;

namespace SentinelMfgSuite.Web.Controllers;

[Authorize(Policy = "SentinelAssessmentAdmin")]
public sealed class InternalReadinessReportController : Controller
{
    [HttpGet("/Internal/AssessmentReport/Sample")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Sample()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        ViewData["DisableThirdPartyScripts"] = true;
        return View("~/Views/InternalReadinessReport/Sample.cshtml", SampleReadinessReportViewModel.Create());
    }
}