using System.Text.Json;
using GymApis.Models.Attendance;
using GymApis.Models.Auth;
using GymApis.Models.Common;
using GymApis.Models.Members;
using GymApis.Models.Membership;
using GymApis.Models.Messaging;
using GymApis.Models.Settings;
using GymApis.Models.Site;
using GymApis.Models.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GymApis.Repos;

/// <summary>
/// EF Core mapped onto the hand-written schema. There are NO migrations and
/// there never will be: schema_v1.sql and the numbered migration files are the
/// source of truth. Entities are added module by module as screens need them.
/// </summary>
public class GymDbContext : DbContext
{
    public GymDbContext(DbContextOptions<GymDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<StaffMember> Staff => Set<StaffMember>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<StaffTag> StaffTags => Set<StaffTag>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<StaffShiftAssignment> StaffShiftAssignments => Set<StaffShiftAssignment>();
    public DbSet<PtAssignment> PtAssignments => Set<PtAssignment>();
    public DbSet<MembershipPlan> MembershipPlans => Set<MembershipPlan>();
    public DbSet<PlanService> PlanServices => Set<PlanService>();
    public DbSet<PlanServiceLink> MembershipPlanServices => Set<PlanServiceLink>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<MemberOverview> MemberOverviews => Set<MemberOverview>();
    public DbSet<StaffAttendance> StaffAttendance => Set<StaffAttendance>();
    public DbSet<StaffAttendanceMonthly> StaffAttendanceMonthly => Set<StaffAttendanceMonthly>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();

    // Public website CMS (008_public_site.sql).
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<SiteMedia> SiteMedia => Set<SiteMedia>();
    public DbSet<SiteOffer> SiteOffers => Set<SiteOffer>();
    public DbSet<SiteTransformation> SiteTransformations => Set<SiteTransformation>();
    public DbSet<SiteEvent> SiteEvents => Set<SiteEvent>();

    // Messaging (009_messaging.sql).
    public DbSet<QueuedMessage> MessageQueue => Set<QueuedMessage>();
    public DbSet<MessageQuotaUsage> MessageQuotaUsage => Set<MessageQuotaUsage>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("citext");

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            // Soft delete is the schema-wide convention — never hard-delete anyone
            // with attendance or payment history attached.
            e.HasQueryFilter(u => u.DeletedAt == null);
            e.Property(u => u.Email).HasColumnType("citext");
            // Written by the set_updated_at() trigger, not by us.
            e.Property(u => u.CreatedAt).ValueGeneratedOnAdd();
            e.Property(u => u.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.Property(t => t.FamilyId).HasDefaultValueSql("gen_random_uuid()");
            e.Property(t => t.CreatedAt).ValueGeneratedOnAdd();
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId);
        });

        b.Entity<StaffMember>(e =>
        {
            e.ToTable("staff");
            e.HasQueryFilter(s => s.DeletedAt == null);
            e.Property(s => s.Email).HasColumnType("citext");
            e.Property(s => s.CreatedAt).ValueGeneratedOnAdd();
            e.Property(s => s.UpdatedAt).ValueGeneratedOnAddOrUpdate();
            e.HasMany(s => s.StaffTags).WithOne().HasForeignKey(t => t.StaffId);
            e.HasMany(s => s.ShiftAssignments).WithOne().HasForeignKey(a => a.StaffId);
        });

        b.Entity<Tag>(e => e.ToTable("tags"));

        b.Entity<StaffTag>(e =>
        {
            e.ToTable("staff_tags");
            e.HasKey(t => new { t.StaffId, t.TagId });      // composite PK, no surrogate id
            e.HasOne(t => t.Tag).WithMany().HasForeignKey(t => t.TagId);
        });

        b.Entity<Shift>(e => e.ToTable("shifts"));

        b.Entity<StaffShiftAssignment>(e =>
        {
            e.ToTable("staff_shift_assignments");
            e.Property(a => a.CreatedAt).ValueGeneratedOnAdd();
            e.Property(a => a.EffectiveFrom).HasDefaultValueSql("CURRENT_DATE");
            e.HasOne(a => a.Shift).WithMany().HasForeignKey(a => a.ShiftId);
        });

        b.Entity<PtAssignment>(e => e.ToTable("pt_assignments"));

        b.Entity<MembershipPlan>(e =>
        {
            e.ToTable("membership_plans");
            // Plans archive rather than delete — same convention as DeletedAt
            // elsewhere, different column name. Getting this wrong resurrects
            // archived plans in every list.
            e.HasQueryFilter(p => p.ArchivedAt == null);
            e.Property(p => p.CreatedAt).ValueGeneratedOnAdd();
            e.Property(p => p.UpdatedAt).ValueGeneratedOnAddOrUpdate();
            e.HasMany(p => p.PlanServices).WithOne().HasForeignKey(s => s.PlanId);
        });

        b.Entity<PlanService>(e => e.ToTable("plan_services"));

        b.Entity<PlanServiceLink>(e =>
        {
            e.ToTable("membership_plan_services");
            e.HasKey(s => new { s.PlanId, s.ServiceId });   // composite PK, no surrogate id
            e.HasOne(s => s.Service).WithMany().HasForeignKey(s => s.ServiceId);
        });

        b.Entity<Member>(e =>
        {
            e.ToTable("members");
            e.HasQueryFilter(m => m.DeletedAt == null);
            e.Property(m => m.Email).HasColumnType("citext");
            e.Property(m => m.CreatedAt).ValueGeneratedOnAdd();
            e.Property(m => m.UpdatedAt).ValueGeneratedOnAddOrUpdate();
            e.Property(m => m.JoinedOn).HasDefaultValueSql("CURRENT_DATE");
            e.HasMany(m => m.Memberships).WithOne().HasForeignKey(x => x.MemberId);
        });

        b.Entity<Membership>(e =>
        {
            e.ToTable("memberships");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
            // total_amount is GENERATED ALWAYS AS (plan_price - discount_amount)
            // STORED. Postgres computes it and rejects any INSERT or UPDATE that
            // supplies a value, so EF must read the column and never write it.
            e.Property(x => x.TotalAmount)
                .ValueGeneratedOnAddOrUpdate()
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
            e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId);
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
            // NOT ValueGeneratedOnAdd: the desk can back-date a payment taken
            // earlier, and letting the store generate this would mean whether
            // that value survived depended on EF's sentinel rules. Every write
            // path sets PaidAt explicitly instead.
        });

        // Read-only projection: the view owns the "latest membership" LATERAL,
        // the membership_state derivation and the deleted_at filter.
        b.Entity<MemberOverview>(e =>
        {
            e.HasNoKey();
            e.ToView("v_member_overview");
        });

        b.Entity<StaffAttendance>(e =>
        {
            e.ToTable("staff_attendance");
            e.Property(a => a.CreatedAt).ValueGeneratedOnAdd();
            e.Property(a => a.UpdatedAt).ValueGeneratedOnAddOrUpdate();
            // Optional: the shift snapshot is null for a row marked before
            // anyone was rostered, so this cannot be a required navigation.
            // There is no Staff navigation on purpose — see the entity.
            e.HasOne(a => a.Shift).WithMany().HasForeignKey(a => a.ShiftId);
        });

        // Read-only projection: the view owns the monthly bucketing and the
        // present/absent/percentage arithmetic behind the detail page's cards.
        b.Entity<StaffAttendanceMonthly>(e =>
        {
            e.HasNoKey();
            e.ToView("v_staff_attendance_monthly");
        });

        // A read-only sliver of `branches`, mapped for one column: `timezone`,
        // which is what lets attendance mean the gym's calendar and not UTC.
        b.Entity<Branch>(e => e.ToTable("branches"));

        // A read-only sliver of `tenants`, mapped for the three brand columns
        // the public website reads instead of copying.
        b.Entity<Tenant>(e => e.ToTable("tenants"));

        b.Entity<ActivityLog>(e =>
        {
            e.ToTable("activity_log");
            e.Property(a => a.Metadata).HasColumnType("jsonb");
            e.Property(a => a.CreatedAt).ValueGeneratedOnAdd();
        });

        // --------------------------------------------------------------------
        // Public website CMS. Note what none of these have: a global query
        // filter. `is_active` on these tables is an editor's visibility toggle,
        // not a soft delete — the CMS must list the rows it has hidden, and it
        // is the *public* projection's job to filter them. A query filter here
        // would make the admin screens unable to see their own drafts.
        // --------------------------------------------------------------------
        b.Entity<SiteSettings>(e =>
        {
            e.ToTable("site_settings");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<SiteMedia>(e =>
        {
            e.ToTable("site_media");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<SiteOffer>(e =>
        {
            e.ToTable("site_offers");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<SiteTransformation>(e =>
        {
            e.ToTable("site_transformations");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<SiteEvent>(e =>
        {
            e.ToTable("site_events");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        // --------------------------------------------------------------------
        // Messaging. No global query filter here either, and for a different
        // reason than the CMS above: the dispatcher has to see terminal rows to
        // report on them, and the retention purge has to see them to delete
        // them. Hiding anything would break both.
        // --------------------------------------------------------------------
        b.Entity<QueuedMessage>(e =>
        {
            e.ToTable("message_queue");
            e.Property(x => x.ToAddress).HasColumnType("citext");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            // NextAttemptAt is defaulted by the DB but every enqueue sets it
            // explicitly, so a back-dated or delayed message stays possible.
        });

        b.Entity<MessageQuotaUsage>(e =>
        {
            e.ToTable("message_quota_usage");
            e.HasKey(x => new { x.QuotaDate, x.Channel });   // composite PK, no surrogate id
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<AppSettings>(e =>
        {
            e.ToTable("app_settings");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });
    }
}
