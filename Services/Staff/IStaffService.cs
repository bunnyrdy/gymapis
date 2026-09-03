using GymApis.Dtos;

namespace GymApis.Services.Staff;

/// <summary>
/// Outcome of a write. Keeps expected failures out of the exception path.
///
/// The flags exist so a service can distinguish outcomes the controller must
/// map to different status codes without throwing for any of them. Everything
/// here is an expected failure: a 400 is the default, and Missing / Denied /
/// Clash say "this one is a 404 / 403 / 409 instead". Both extra flags default
/// to false, so callers written before they existed behave identically.
/// </summary>
public record ServiceResult<T>(
    bool Succeeded,
    T? Value,
    string? Error,
    bool NotFound = false,
    bool Forbidden = false,
    bool Conflict = false)
{
    public static ServiceResult<T> Ok(T value) => new(true, value, null);
    public static ServiceResult<T> Fail(string error) => new(false, default, error);
    public static ServiceResult<T> Missing() => new(false, default, "Not found.", NotFound: true);

    /// <summary>The caller is authenticated but not allowed to do this. 403.</summary>
    public static ServiceResult<T> Denied(string error) => new(false, default, error, Forbidden: true);

    /// <summary>The write collides with a record that already exists. 409.</summary>
    public static ServiceResult<T> Clash(string error) => new(false, default, error, Conflict: true);
}

public interface IStaffService
{
    Task<PagedResult<StaffListItem>> ListAsync(StaffQuery query, CancellationToken ct);
    Task<StaffResponse?> GetAsync(long id, CancellationToken ct);
    Task<ServiceResult<StaffResponse>> CreateAsync(StaffWriteRequest req, IFormFile? photo, CancellationToken ct);
    Task<ServiceResult<StaffResponse>> UpdateAsync(long id, StaffWriteRequest req, IFormFile? photo, bool removePhoto, CancellationToken ct);
    Task<ServiceResult<bool>> DeleteAsync(long id, CancellationToken ct);

    /// <summary>Counts for the list's cards, over the whole branch — not one page.</summary>
    Task<StaffStatsResponse> StatsAsync(CancellationToken ct);

    /// <summary>DPDP erasure: overwrite personal data, keep the employment trail.</summary>
    Task<ServiceResult<bool>> EraseAsync(long id, CancellationToken ct);
}

/// <summary>Marker interfaces so DI can hand each controller the right role.</summary>
public interface IReceptionistService : IStaffService;
public interface ITrainerService : IStaffService;
