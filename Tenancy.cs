namespace GymApis;

/// <summary>
/// V1 runs for ONE client, ONE branch (see the scope note at the top of
/// schema_v1.sql). Every business table already carries tenant_id + branch_id,
/// but no UI exposes them, so the values are pinned here.
///
/// ponytail: hardcoded tenant/branch, no Row-Level Security. When tenant #2
/// arrives, apply rls.sql and replace these constants with a scoped
/// ITenantContext read off the JWT + a connection interceptor that runs
/// set_config('app.tenant_id', ...) — the QuickBills project has that pattern.
/// </summary>
public static class Tenancy
{
    public const long TenantId = 1;
    public const long BranchId = 1;
}
