namespace GymApis.Services;

/// <summary>
/// Named authorization policies.
///
/// SECURITY: staff management is a privilege-escalation surface. A receptionist
/// who could create staff could create an admin; a trainer who could edit staff
/// could change their own record. Only the three managerial roles get in, and
/// the policy is stated once here rather than re-typed per controller.
/// </summary>
public static class AuthPolicies
{
    public const string ManageStaff = nameof(ManageStaff);

    /// <summary>
    /// Membership plans set what the gym charges. Same managerial roles as
    /// staff — a receptionist who could edit plans could sell a $0 membership.
    /// Named separately so the two can diverge without touching every [Authorize].
    /// </summary>
    public const string ManagePlans = nameof(ManagePlans);

    /// <summary>
    /// Registering and editing members. This is the first policy whose role
    /// list differs from ManageStaff, and the difference is the point: signing
    /// up a walk-in is exactly what a receptionist is at the desk to do.
    /// </summary>
    /// <summary>
    /// Erasing a staff member's personal data. Narrower than ManageStaff by one
    /// rung — the same shape as EraseMembers narrowing ManageMembers. A manager
    /// hires and edits; wiping an employment record is an owner/admin act, and
    /// it cannot be undone.
    /// </summary>
    public const string EraseStaff = nameof(EraseStaff);

    public const string ManageMembers = nameof(ManageMembers);

    /// <summary>
    /// Deleting a member and erasing their personal data. Kept managerial:
    /// erasure is irreversible and removal changes what the roster reports,
    /// neither of which belongs to the person taking payments at the counter.
    /// </summary>
    public const string EraseMembers = nameof(EraseMembers);

    /// <summary>
    /// Taking the daily register. The one staff-adjacent right a receptionist
    /// holds, and deliberately so: the front desk is who sees people arrive.
    ///
    /// It is safe to widen here because marking cannot escalate anything — the
    /// request carries a staff id, a date and present-or-absent, and the server
    /// derives everything else. Note what is NOT covered: changing a record
    /// that already exists stays on ManageStaff, because rewriting the
    /// employment log is a different act from writing it down the first time.
    /// </summary>
    public const string MarkAttendance = nameof(MarkAttendance);

    /// <summary>
    /// Seeing what the gym earns: the dashboard's Monthly Revenue figure and
    /// the outstanding-balance total.
    ///
    /// Unlike every other policy here this one does not guard an endpoint. The
    /// dashboard is the landing page for every role, and a receptionist has
    /// legitimate business with most of it — the expiring list is their work
    /// queue. So the controller stays open to any authenticated user and
    /// DashboardService omits the two money fields for anyone outside these
    /// roles. The role comes off the validated token via ICurrentUser, never
    /// off the request.
    ///
    /// Note it is the *totals* that are restricted, not the counts. "12 members
    /// owe something" is a work queue; "₹1,25,000 is outstanding" is a business
    /// figure, and the receptionist can already see any single member's balance
    /// on the member they are serving.
    /// </summary>
    public const string ViewRevenue = nameof(ViewRevenue);

    /// <summary>
    /// Editing the public website: the hero, the gallery, the offers, the
    /// success stories, the events.
    ///
    /// Managerial rather than front-desk, and not because the CMS is dangerous
    /// to the database — it is dangerous to the business. Everything written
    /// through it is published to anyone on the internet under the gym's name,
    /// and one of the tables carries members' photographs. That is the same
    /// class of decision as setting prices, so it sits with ManagePlans rather
    /// than ManageMembers.
    /// </summary>
    public const string ManageSite = nameof(ManageSite);

    /// <summary>
    /// Operational settings: whether the gym sends email at all, how much of
    /// the daily allowance the reminder queue may spend, how far ahead members
    /// are reminded.
    ///
    /// Owner and admin only — narrower than ManageSite, and narrower than the
    /// managerial default. A receptionist who could reach this could silence
    /// every renewal reminder the gym sends, and (with the second toggle) every
    /// password-reset link, from a screen with a Save button on it. The blast
    /// radius is the whole tenant's outbound mail, which is a different class
    /// of decision from editing a member.
    /// </summary>
    public const string ManageSettings = nameof(ManageSettings);

    public static readonly string[] ManageStaffRoles = ["owner", "admin", "manager"];

    public static readonly string[] EraseStaffRoles = ["owner", "admin"];

    public static readonly string[] ManagePlansRoles = ["owner", "admin", "manager"];

    public static readonly string[] ManageMembersRoles = ["owner", "admin", "manager", "receptionist"];

    public static readonly string[] EraseMembersRoles = ["owner", "admin", "manager"];

    public static readonly string[] MarkAttendanceRoles = ["owner", "admin", "manager", "receptionist"];

    public static readonly string[] ViewRevenueRoles = ["owner", "admin", "manager"];

    public static readonly string[] ManageSiteRoles = ["owner", "admin", "manager"];

    public static readonly string[] ManageSettingsRoles = ["owner", "admin"];
}
