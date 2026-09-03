namespace GymApis.Dtos;

// ---------------------------------------------------------------------------
// Dashboard — read models only.
//
// The module has no write path: every figure on the screen is owned by the
// module that produces it, and the dashboard composes them. That is why there
// is no DashboardEnums block and no IValidatableObject request here — the one
// endpoint takes no input.
//
// The whole screen is one response. Six cards, a table, a donut, an attendance
// widget and a feed would be five round trips if they were five endpoints, and
// they all describe the same instant — served separately they can disagree
// with each other on a busy morning.
// ---------------------------------------------------------------------------

/// <summary>Everything the dashboard screen renders, as one document.</summary>
public record DashboardResponse(
    DashboardKpis                          Kpis,
    MembershipMix                          Mix,
    IReadOnlyList<ExpiringMembershipItem>  Expiring,
    DashboardAttendance                    Attendance,
    IReadOnlyList<ActivityItem>            Activity);

/// <summary>
/// The six cards along the top.
///
/// The two money fields are nullable because they are <b>authorization</b>, not
/// absence: they are null for anyone outside AuthPolicies.ViewRevenueRoles.
/// One response shape either way, and the client renders the cards it was given
/// rather than re-deciding who may see them — a second copy of the rule on the
/// client is a second place for it to be wrong.
///
/// <see cref="PendingPayments"/> stays visible to everyone. A count is a work
/// queue ("twelve people owe something"); the total is a business figure.
/// </summary>
public record DashboardKpis(
    int      ActiveMembers,
    int      NewMembersThisMonth,
    int      ExpiringSoon,
    int      Expired,
    int      PendingPayments,
    decimal? PendingPaymentsValue,
    decimal? MonthlyRevenue);

/// <summary>
/// The donut. Three slices over <see cref="Total"/>, which is the number of
/// active members holding a membership — not the member count, or the arcs
/// would not close.
/// </summary>
public record MembershipMix(
    int Active,
    int ExpiringSoon,
    int Expired,
    int Total);

/// <summary>
/// One row of "Expiring Memberships (7 Days)".
///
/// Already-expired memberships are included: they are the most urgent thing on
/// the list, and a table that hid them would send the front desk chasing the
/// member with three days left while ignoring the one who lapsed on Tuesday.
/// <see cref="DaysRemaining"/> is negative for those, and the client renders
/// the badge from it.
/// </summary>
public record ExpiringMembershipItem(
    long      MemberId,
    string    FullName,
    string?   PhotoUrl,
    string?   PlanName,
    DateOnly? EndDate,
    int?      DaysRemaining,
    string    MembershipState);

/// <summary>
/// Today's staff register, counted over the whole branch.
///
/// Members are deliberately absent. `member_checkins` exists in the schema but
/// nothing writes to it until a check-in module lands, so a member figure here
/// would be a permanent zero dressed up as data.
/// </summary>
public record DashboardAttendance(
    int                                TotalStaff,
    int                                Present,
    int                                Absent,
    int                                NotMarked,
    IReadOnlyList<StaffNotMarkedItem>  NotMarkedStaff);

/// <summary>One name in "Staff Not Marked Today", with what Mark needs.</summary>
public record StaffNotMarkedItem(
    long    StaffId,
    string  FullName,
    string  Role,
    string? PhotoUrl);

/// <summary>
/// One entry in the Recent Activity feed, straight off `activity_log`.
///
/// <see cref="ActorEmail"/> is joined from `users` because the log stores only
/// `actor_user_id` — there is no denormalised actor name — and it is null for
/// rows written by the system (the attendance closeout has no actor) or by a
/// user since deleted.
/// </summary>
public record ActivityItem(
    long           Id,
    string         Action,
    string         EntityType,
    long?          EntityId,
    string         Description,
    DateTimeOffset CreatedAt,
    string?        ActorEmail);
