namespace GymApis.Models.Common;

/// <summary>
/// Maps onto `tenants`. A read-only sliver, the same shape as
/// <see cref="Branch"/> — entities get added as screens need them, and until
/// the public website nothing had needed this one.
///
/// It exists for three columns. `name`, `tagline` and `logo_url` are the gym's
/// brand, and schema_v1.sql has said since day one that the Settings screen
/// edits them here. The website reads them rather than keeping its own copy;
/// see the banner in 008_public_site.sql for why that matters.
///
/// Nothing writes to this table yet. When the Settings screen lands it will,
/// and it will be the one screen that writes two rows — this and `branches`.
/// </summary>
public class Tenant
{
    public long Id { get; set; }

    public string Name { get; set; } = default!;
    public string? Tagline { get; set; }
    public string? LogoUrl { get; set; }
}
