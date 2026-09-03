namespace GymApis.Models.Common;

/// <summary>
/// Maps onto `activity_log` — the accountability trail. Who created, changed or
/// removed a record, and when. Written on every mutation; never updated or
/// deleted by application code.
/// </summary>
public class ActivityLog
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long? BranchId { get; set; }
    public long? ActorUserId { get; set; }

    /// <summary>'staff.created', 'staff.updated', 'staff.deleted'.</summary>
    public string Action { get; set; } = default!;
    public string EntityType { get; set; } = default!;
    public long? EntityId { get; set; }
    public string Description { get; set; } = default!;
    public string Metadata { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
