using GymApis.Dtos;
using GymApis.Services;
using GymApis.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GymApis.Controllers.Payments;

/// <summary>
/// The centralized payments console. Two GETs, no write verbs — a payment is
/// still recorded through
/// <c>POST /api/members/{id}/memberships/{membershipId}/payments</c>, so the
/// overpay check and the audit entry live in one place.
///
/// The policy is ManageMembers, not ViewRevenue, and that is deliberate. Money
/// here is a field-level privilege exactly as it is on the dashboard: the front
/// desk takes the payments and works the pending queue, so they need the page;
/// PaymentService nulls the three money totals for anyone outside
/// ViewRevenueRoles. Gating the endpoint instead would also produce a page where
/// two of five tabs work, because the Pending Payments and Expired Memberships
/// tabs read /api/members.
/// </summary>
[ApiController]
[Route("api/payments")]
[Authorize(Policy = AuthPolicies.ManageMembers)]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _service;

    public PaymentsController(IPaymentService service) => _service = service;

    /// <summary>The transaction ledger, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<PaymentLedgerItem>>> List(
        [FromQuery] PaymentQuery query, CancellationToken ct) =>
        Ok(await _service.ListAsync(query, ct));

    /// <summary>
    /// The four cards. Takes the same query as the list so the two figures that
    /// describe the visible rows move with the filter bar.
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<PaymentStatsResponse>> Stats(
        [FromQuery] PaymentQuery query, CancellationToken ct) =>
        Ok(await _service.StatsAsync(query, ct));
}
