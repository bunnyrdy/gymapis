namespace GymApis.Models.Staff;

/// <summary>
/// Maps onto `pt_assignments` — a member assigned to a trainer for personal
/// training. The trainer screens read only the active count; the roster and the
/// session ledger are their own module.
/// </summary>
public class PtAssignment
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long TrainerId { get; set; }
    public long MemberId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? SessionsTotal { get; set; }
    public int SessionsUsed { get; set; }
    public decimal? Fee { get; set; }

    /// <summary>active | completed | cancelled</summary>
    public string Status { get; set; } = "active";
}
