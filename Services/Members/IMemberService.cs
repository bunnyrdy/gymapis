using GymApis.Dtos;
using GymApis.Services.Staff;   // ServiceResult<T> — generic, shared across modules

namespace GymApis.Services.Members;

public interface IMemberService
{
    Task<PagedResult<MemberListItem>> ListAsync(MemberQuery query, CancellationToken ct);
    Task<MemberStatsResponse> StatsAsync(CancellationToken ct);
    Task<MemberResponse?> GetAsync(long id, CancellationToken ct);

    Task<ServiceResult<MemberResponse>> CreateAsync(
        MemberWriteRequest request, IFormFile? photo, CancellationToken ct);

    Task<ServiceResult<MemberResponse>> UpdateAsync(
        long id, MemberWriteRequest request, IFormFile? photo, bool removePhoto, CancellationToken ct);

    /// <summary>Activates or deactivates. Reversible; nothing else is touched.</summary>
    Task<ServiceResult<bool>> SetStatusAsync(long id, string status, CancellationToken ct);

    /// <summary>DPDP erasure: overwrite personal data, keep the financial trail.</summary>
    Task<ServiceResult<bool>> EraseAsync(long id, CancellationToken ct);

    /// <summary>Sells a new membership to an existing member (the Renew screen).</summary>
    Task<ServiceResult<MemberResponse>> RenewAsync(
        long id, MembershipWriteRequest request, CancellationToken ct);

    /// <summary>Edits an existing membership: plan change, extension, expiry or discount.</summary>
    Task<ServiceResult<MemberResponse>> UpdateMembershipAsync(
        long memberId, long membershipId, MembershipUpdateRequest request, CancellationToken ct);

    /// <summary>Adds one payment to an existing membership (partial payments).</summary>
    Task<ServiceResult<MemberResponse>> AddPaymentAsync(
        long memberId, long membershipId, PaymentWriteRequest request, CancellationToken ct);
}
