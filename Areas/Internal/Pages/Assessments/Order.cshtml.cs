using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Payments;

namespace SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments;

[Authorize(Policy = "SentinelAssessmentAdmin")]
public sealed class OrderModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IAssessmentOrderService _orders;
    private readonly IInvitationPaymentAuthorizer _authorizer;

    public OrderModel(ApplicationDbContext db, IAssessmentOrderService orders, IInvitationPaymentAuthorizer authorizer)
    {
        _db = db;
        _orders = orders;
        _authorizer = authorizer;
    }

    [BindProperty(SupportsGet = true)]
    public Guid AssessmentId { get; set; }

    [BindProperty]
    public string? PaymentReference { get; set; }

    public Assessment? Assessment { get; private set; }
    public AssessmentOrder? Order { get; private set; }
    public IReadOnlyList<AssessmentPaymentEvent> Events { get; private set; } = Array.Empty<AssessmentPaymentEvent>();
    public InvitationAuthorization Eligibility { get; private set; } = new(false, "Unknown");
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        return await LoadAsync(ct) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostVerifyBankTransferAsync(Guid orderId, CancellationToken ct)
    {
        var admin = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name;
        var result = await _orders.VerifyBankTransferAsync(orderId, admin ?? string.Empty, PaymentReference ?? string.Empty, ct);
        Message = result.Succeeded ? "Bank transfer verified." : result.FailureMessage;
        return await LoadAsync(ct) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        Assessment = await _db.Set<Assessment>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == AssessmentId, ct);
        if (Assessment is null) return false;
        Order = await _db.Set<AssessmentOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.AssessmentId == AssessmentId, ct);
        if (Order is not null)
        {
            Events = await _db.Set<AssessmentPaymentEvent>().AsNoTracking()
                .Where(e => e.OrderId == Order.Id).OrderBy(e => e.OccurredUtc).ToListAsync(ct);
        }
        Eligibility = await _authorizer.EvaluateAsync(AssessmentId, ct);
        return true;
    }
}
