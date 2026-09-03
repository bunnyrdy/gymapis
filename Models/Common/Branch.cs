namespace GymApis.Models.Common;

/// <summary>
/// Maps onto `branches`. V1 pins the branch to a constant (see
/// <see cref="Tenancy"/>) and no screen edits one, so this is deliberately a
/// read-only sliver of the table rather than the whole thing — entities get
/// added as screens need them.
///
/// It existed for one column. `timezone` is what makes "today" and "end of day"
/// mean the gym's calendar rather than UTC, which the attendance module cannot
/// do without. See <see cref="Services.Attendance.BranchClock"/>.
///
/// The public website added the second reason. The Contact page needs the gym's
/// phone, email and address, and those columns have been on `branches` since
/// schema_v1 — so the site reads them here rather than keeping a website copy
/// that drifts from the one the desk answers. The rest of the table (postal
/// code, country) is still unmapped; add a property when a screen needs it.
///
/// The property is spelled Timezone, one word, so the snake_case convention
/// produces `timezone` and not `time_zone`.
/// </summary>
public class Branch
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    public string Name { get; set; } = default!;
    public string Code { get; set; } = default!;

    /// <summary>IANA name, e.g. 'Asia/Kolkata'. Seeded per branch.</summary>
    public string Timezone { get; set; } = default!;

    public string Currency { get; set; } = default!;
    public bool IsActive { get; set; } = true;

    // Contact + location. Read by the public site's Contact page and footer;
    // edited on the admin Settings screen, which is the one screen that writes
    // to two rows (this one and `tenants`).
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
}
