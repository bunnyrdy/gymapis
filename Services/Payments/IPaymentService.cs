using GymApis.Dtos;

namespace GymApis.Services.Payments;

/// <summary>
/// The payments console. Reads only — <c>POST /api/members/{id}/memberships/
/// {membershipId}/payments</c> remains the one way money is recorded, so there
/// is exactly one write path to audit and to keep the balance view honest.
/// </summary>
public interface IPaymentService
{
    Task<PagedResult<PaymentLedgerItem>> ListAsync(PaymentQuery query, CancellationToken ct);

    Task<PaymentStatsResponse> StatsAsync(PaymentQuery query, CancellationToken ct);

    /// <summary>
    /// Completed payments taken in the branch's current month. Lives here
    /// rather than on the dashboard because both screens need the same figure,
    /// and two copies of it is how the payments page and the Monthly Revenue
    /// card start disagreeing.
    /// </summary>
    Task<decimal> MonthlyCollectionAsync(DateOnly today, CancellationToken ct);
}
