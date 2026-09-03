using GymApis.Dtos;

namespace GymApis.Services.Dashboard;

public interface IDashboardService
{
    /// <summary>
    /// The whole dashboard screen in one document. Read-only, and money fields
    /// come back null for anyone outside AuthPolicies.ViewRevenueRoles.
    /// </summary>
    Task<DashboardResponse> SummaryAsync(CancellationToken ct);
}
