using GymApis.Dtos;
using GymApis.Services.Staff;   // ServiceResult<T> — generic, shared across modules

namespace GymApis.Services.Membership;

public interface IMembershipPlanService
{
    Task<PagedResult<MembershipPlanListItem>> ListAsync(MembershipPlanQuery query, CancellationToken ct);
    Task<MembershipPlanResponse?> GetAsync(long id, CancellationToken ct);
    Task<ServiceResult<MembershipPlanResponse>> CreateAsync(MembershipPlanWriteRequest req, CancellationToken ct);
    Task<ServiceResult<MembershipPlanResponse>> UpdateAsync(long id, MembershipPlanWriteRequest req, CancellationToken ct);
    Task<ServiceResult<bool>> ArchiveAsync(long id, CancellationToken ct);

    /// <summary>
    /// Active plans as the public website renders them. Here rather than in the
    /// site module so plan scoping stays in the one place that owns it — the
    /// same rule DashboardService follows when it reuses MemberService.StatsAsync
    /// instead of recounting.
    /// </summary>
    Task<IReadOnlyList<PublicPlan>> PublicPlansAsync(CancellationToken ct);
}
