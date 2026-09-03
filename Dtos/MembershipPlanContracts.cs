using System.ComponentModel.DataAnnotations;

namespace GymApis.Dtos;

/// <summary>
/// Allowed values, mirroring the CHECK constraints in schema_v1.sql. Validating
/// here means a bad value returns 400 with a useful message instead of letting
/// Postgres raise a constraint violation that surfaces as a 500.
/// </summary>
public static class PlanEnums
{
    public static readonly string[] DurationUnits = ["day", "week", "month"];
}

/// <summary>
/// The write model for a membership plan.
///
/// SECURITY — what is deliberately NOT here: Id, TenantId, BranchId, ArchivedAt
/// and CreatedAt. Those are set by the server. Binding straight to the entity
/// would let a caller post {"tenantId":2} and write into another tenant, or
/// {"archivedAt":null} to resurrect a deleted plan. The DTO is the allow-list.
///
/// Features is absent too: the Included Services grid is relational
/// (<see cref="ServiceIds"/>), and `membership_plans.features` is reserved for
/// free-text bullets the client does not write.
/// </summary>
public class MembershipPlanWriteRequest : IValidatableObject
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string Name { get; set; } = default!;

    /// <summary>
    /// The form's "Internal Plan Code". Optional — left blank, the server
    /// generates the next PLN-NNNN.
    /// </summary>
    [StringLength(40)]
    public string? PlanCode { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    [Range(1, 3650)]
    public int DurationValue { get; set; } = 1;

    public string DurationUnit { get; set; } = "month";

    [Range(0, 999999.99)]
    public decimal Price { get; set; }

    /// <summary>
    /// Ids from `plan_services`. Existence, tenant and is_active are checked in
    /// the service — an id from a request body is never trusted.
    /// </summary>
    public long[] ServiceIds { get; set; } = [];

    /// <summary>The form's "Visibility Status" toggle.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The public site's "MOST POPULAR" badge. Stored rather than derived —
    /// which plan the gym pushes is a marketing choice, not a function of price.
    /// </summary>
    public bool IsFeatured { get; set; }

    public short DisplayOrder { get; set; }

    /// <summary>
    /// Cross-field rules that attributes cannot express. Runs automatically
    /// under [ApiController], and mirrors the DB CHECK constraints so bad input
    /// is a 400 rather than a 500.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (!PlanEnums.DurationUnits.Contains(DurationUnit))
            yield return new ValidationResult(
                $"Duration unit must be one of: {string.Join(", ", PlanEnums.DurationUnits)}.",
                [nameof(DurationUnit)]);

        if (ServiceIds.Length > 30)
            yield return new ValidationResult(
                "A plan cannot include more than 30 services.", [nameof(ServiceIds)]);

        if (ServiceIds.Length != ServiceIds.Distinct().Count())
            yield return new ValidationResult(
                "The same service was included twice.", [nameof(ServiceIds)]);

        if (!string.IsNullOrWhiteSpace(PlanCode) &&
            !System.Text.RegularExpressions.Regex.IsMatch(PlanCode, @"^[A-Za-z0-9\-]{2,40}$"))
            yield return new ValidationResult(
                "Plan code may contain only letters, numbers and hyphens.", [nameof(PlanCode)]);
    }
}

/// <summary>One row of the Included Services catalog. The plan-side twin of TagResponse.</summary>
public record PlanServiceOption(long Id, string Name);

public record MembershipPlanResponse(
    long    Id,
    string  Name,
    string? PlanCode,
    string? Description,
    int     DurationValue,
    string  DurationUnit,
    decimal Price,
    bool    IsActive,
    bool    IsFeatured,
    short   DisplayOrder,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PlanServiceOption> Services);

/// <summary>Trimmed for the list table — no description, services collapsed to a count.</summary>
public record MembershipPlanListItem(
    long    Id,
    string  Name,
    string? PlanCode,
    int     DurationValue,
    string  DurationUnit,
    decimal Price,
    bool    IsActive,
    bool    IsFeatured,
    short   DisplayOrder,
    int     ServiceCount);

/// <summary>Query string for the plan list. Bounded on purpose — see PageSize.</summary>
public class MembershipPlanQuery
{
    [StringLength(80)]
    public string? Search { get; set; }

    /// <summary>active | inactive. Anything else is ignored rather than erroring.</summary>
    public string? Status { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Capped at 100. Without a ceiling, ?pageSize=1000000 is a free
    /// denial-of-service and a bulk data export in one request.
    /// </summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}
