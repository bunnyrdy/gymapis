-- ============================================================================
-- KINETIC LOGIC — GYM MANAGEMENT SYSTEM
-- Schema v1  |  PostgreSQL 14+
-- ============================================================================
-- SCOPE OF V1
--   Ships for ONE client, ONE branch. No tenant-management or branch-management
--   UI is built. But every business table carries tenant_id + branch_id from
--   day one, because retrofitting those is the one migration that genuinely
--   hurts. In V1 the app hardcodes tenant_id = 1, branch_id = 1 and no user
--   ever sees either.
--
-- CONVENTIONS
--   * bigint identity PKs; human-facing codes (MEM-0001, TRN-001) are separate
--   * text + CHECK instead of ENUM — adding a status later is a one-line ALTER
--   * soft delete via deleted_at — never hard-delete anyone with attendance
--     or payment history attached
--   * money as numeric(10,2), never float
--   * all uniqueness is scoped per-tenant, never global
--
-- APP-LAYER RULE (not enforceable by a constraint, so enforce it in code):
--   every query filters on tenant_id. See rls.sql for the DB-level backstop
--   to switch on when tenant #2 arrives.
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS citext;      -- case-insensitive email
CREATE EXTENSION IF NOT EXISTS pg_trgm;     -- fast ILIKE search on names

CREATE OR REPLACE FUNCTION set_updated_at() RETURNS trigger AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;


-- ===========================================================================
-- 1. TENANTS & BRANCHES
-- ===========================================================================
-- A tenant is the gym business (your paying customer).
-- A branch is one physical location belonging to that tenant.
-- The Settings screen edits the brand fields here and the location fields
-- on branches — for V1 that's one screen writing two rows.

CREATE TABLE tenants (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name            text        NOT NULL,           -- 'Steel Flex'
    slug            citext      NOT NULL UNIQUE,    -- 'steelflex' -> steelflex.yourapp.com
    tagline         text,
    logo_url        text,
    status          text        NOT NULL DEFAULT 'active'
                    CHECK (status IN ('active','suspended','cancelled')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE TRIGGER trg_tenants_updated BEFORE UPDATE ON tenants
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE TABLE branches (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name            text        NOT NULL,           -- 'Main Branch'
    code            text        NOT NULL,           -- 'BR-01'
    phone           text,
    email           citext,
    address_line1   text,
    address_line2   text,
    city            text,
    state           text,
    postal_code     text,
    country         char(2)     NOT NULL DEFAULT 'IN',
    currency        char(3)     NOT NULL DEFAULT 'INR',
    timezone        text        NOT NULL DEFAULT 'Asia/Kolkata',
    is_active       boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, code)
);
CREATE TRIGGER trg_branches_updated BEFORE UPDATE ON branches
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE INDEX idx_branches_tenant ON branches(tenant_id);


-- ===========================================================================
-- 2. AUTH  (Login screen)
-- ===========================================================================
-- Identity + credentials only; human details live in staff/members.
-- Email is unique PER TENANT, not globally — the same person can hold an
-- account at two different gyms. Resolve the tenant from the subdomain
-- (or a hidden default in V1) before checking the password.
--
-- 'member' is already a valid role. When the member portal ships you only
-- populate members.user_id — no schema change.

CREATE TABLE users (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    email           citext      NOT NULL,
    password_hash   text        NOT NULL,
    role            text        NOT NULL
                    CHECK (role IN ('owner','admin','manager','trainer','receptionist','member')),
    is_active       boolean     NOT NULL DEFAULT true,
    last_login_at   timestamptz,
    reset_token_hash text,                          -- "Forgot Password?"
    reset_expires_at timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    deleted_at      timestamptz,
    UNIQUE (tenant_id, email)
);
CREATE TRIGGER trg_users_updated BEFORE UPDATE ON users
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE INDEX idx_users_tenant_role ON users(tenant_id, role) WHERE deleted_at IS NULL;


-- ===========================================================================
-- 3. SHIFTS
-- ===========================================================================
CREATE TABLE shifts (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name            text        NOT NULL,           -- 'Morning','Mid-day','Evening','Flex'
    start_time      time,                           -- NULL for Flex/Custom
    end_time        time,
    is_active       boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, name)
);
CREATE TRIGGER trg_shifts_updated BEFORE UPDATE ON shifts
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();


-- ===========================================================================
-- 4. STAFF  (trainers + receptionists + admins, ONE table)
-- ===========================================================================
-- Add/Edit Trainer and Add/Edit Receptionist are the same form with two
-- fields swapped. One table keeps the shared Attendance page a single query
-- instead of a UNION over two near-identical tables.

CREATE TABLE staff (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id           bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    branch_id           bigint      NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
    user_id             bigint      UNIQUE REFERENCES users(id) ON DELETE SET NULL,
    staff_code          text        NOT NULL,       -- 'TRN-001' / 'REC-004'
    role                text        NOT NULL
                        CHECK (role IN ('trainer','receptionist','manager','admin')),

    -- Personal Information
    full_name           text        NOT NULL,
    gender              text        CHECK (gender IN ('male','female','other','undisclosed')),
    date_of_birth       date,
    phone               text        NOT NULL,
    email               citext,
    address             text,
    photo_url           text,
    emergency_contact_name  text,
    emergency_contact_phone text,

    -- Professional Information
    job_title           text,                       -- 'Head Receptionist'
    specialization      text,                       -- 'Strength & Conditioning'
    qualifications      text[]      NOT NULL DEFAULT '{}',
    experience_years    numeric(4,1) CHECK (experience_years >= 0),
    joining_date        date        NOT NULL,
    status              text        NOT NULL DEFAULT 'active'
                        CHECK (status IN ('active','inactive','on_leave','terminated')),

    notes               text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    deleted_at          timestamptz,
    UNIQUE (tenant_id, staff_code)
);
CREATE TRIGGER trg_staff_updated BEFORE UPDATE ON staff
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE INDEX idx_staff_branch_role ON staff(tenant_id, branch_id, role, status)
    WHERE deleted_at IS NULL;
CREATE INDEX idx_staff_name_trgm ON staff USING gin (full_name gin_trgm_ops);


-- Responsibility / specialization chips: "Check-in", "Tours", "Inventory",
-- "Lead", "Scheduling", "CrossFit L3". A controlled vocabulary keeps the
-- filter dropdowns working and stops typo'd duplicates.
CREATE TABLE tags (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    category        text        NOT NULL
                    CHECK (category IN ('responsibility','specialization')),
    name            text        NOT NULL,
    is_active       boolean     NOT NULL DEFAULT true,
    UNIQUE (tenant_id, category, name)
);

CREATE TABLE staff_tags (
    staff_id        bigint NOT NULL REFERENCES staff(id) ON DELETE CASCADE,
    tag_id          bigint NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY (staff_id, tag_id)
);
CREATE INDEX idx_staff_tags_tag ON staff_tags(tag_id);


-- Which shift someone works, and on which days.
-- Dated rows rather than a column on `staff`: moving a person from Morning to
-- Evening must not silently rewrite what last month's attendance log claims.
CREATE TABLE staff_shift_assignments (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    staff_id          bigint      NOT NULL REFERENCES staff(id) ON DELETE CASCADE,
    shift_id          bigint      NOT NULL REFERENCES shifts(id),
    -- ISO weekdays 1=Mon .. 7=Sun — matches the M T W T F S S toggles
    working_days      smallint[]  NOT NULL
                      CHECK (working_days <@ ARRAY[1,2,3,4,5,6,7]::smallint[]),
    custom_start_time time,                         -- used when shift is Custom
    custom_end_time   time,
    effective_from    date        NOT NULL DEFAULT CURRENT_DATE,
    effective_to      date,                         -- NULL = current assignment
    created_at        timestamptz NOT NULL DEFAULT now(),
    CHECK (effective_to IS NULL OR effective_to >= effective_from)
);
CREATE UNIQUE INDEX idx_staff_current_shift
    ON staff_shift_assignments(staff_id) WHERE effective_to IS NULL;
CREATE INDEX idx_staff_shift_lookup
    ON staff_shift_assignments(staff_id, effective_from DESC);


-- ===========================================================================
-- 5. STAFF ATTENDANCE  (the shared Trainer/Receptionist attendance page)
-- ===========================================================================
-- One row per staff member per day, both roles in the same table — the page
-- just filters on staff.role. shift_id is snapshotted so the log stays
-- truthful after a roster change.

CREATE TABLE staff_attendance (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id           bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    branch_id           bigint      NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
    staff_id            bigint      NOT NULL REFERENCES staff(id) ON DELETE CASCADE,
    attendance_date     date        NOT NULL,
    shift_id            bigint      REFERENCES shifts(id),
    check_in_at         timestamptz,
    check_out_at        timestamptz,
    status              text        NOT NULL
                        CHECK (status IN ('present','absent','late','half_day','leave','holiday','week_off')),
    leave_reason        text,                       -- 'Sick Leave'
    is_late             boolean     NOT NULL DEFAULT false,   -- the "(Late)" badge
    marked_by_user_id   bigint      REFERENCES users(id) ON DELETE SET NULL,
    marked_via          text        NOT NULL DEFAULT 'manual'
                        -- 'system' = closed out automatically after the shift
                        -- ended with nobody marking it; those rows carry no
                        -- marked_by_user_id. See 005_attendance_module.sql.
                        CHECK (marked_via IN ('manual','biometric','qr','mobile','system')),
    notes               text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    UNIQUE (staff_id, attendance_date),
    CHECK (check_out_at IS NULL OR check_in_at IS NULL OR check_out_at >= check_in_at)
);
CREATE TRIGGER trg_staff_attendance_updated BEFORE UPDATE ON staff_attendance
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- "Present Today / Absent Today / Not Marked" dashboard cards
CREATE INDEX idx_attendance_branch_date
    ON staff_attendance(tenant_id, branch_id, attendance_date, status);
-- monthly calendar + attendance % for one person
CREATE INDEX idx_attendance_staff_date
    ON staff_attendance(staff_id, attendance_date DESC);


-- ===========================================================================
-- 6. MEMBERSHIP PLANS
-- ===========================================================================
-- branch_id NULL = plan sold at every branch (the normal case).
CREATE TABLE membership_plans (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    branch_id       bigint      REFERENCES branches(id) ON DELETE CASCADE,
    name            text        NOT NULL,           -- 'Elite Annual Membership'
    plan_code       text,                           -- 'PLN-ANN-01'
    description     text,
    duration_value  integer     NOT NULL CHECK (duration_value > 0),
    duration_unit   text        NOT NULL CHECK (duration_unit IN ('day','week','month')),
    price           numeric(10,2) NOT NULL CHECK (price >= 0),
    features        text[]      NOT NULL DEFAULT '{}',   -- the checkmark bullets
    is_active       boolean     NOT NULL DEFAULT true,   -- Visibility Status
    display_order   smallint    NOT NULL DEFAULT 0,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    archived_at     timestamptz,                    -- Settings "archive" action
    UNIQUE (tenant_id, plan_code)
);
CREATE TRIGGER trg_plans_updated BEFORE UPDATE ON membership_plans
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE INDEX idx_plans_tenant_active ON membership_plans(tenant_id, is_active)
    WHERE archived_at IS NULL;

-- Expiry lives in the DB so the API and any future mobile client can't
-- disagree:  end_date = start_date + plan_interval(...) - 1 day
CREATE OR REPLACE FUNCTION plan_interval(p_value int, p_unit text)
RETURNS interval AS $$
    SELECT CASE p_unit
        WHEN 'day'   THEN make_interval(days   => p_value)
        WHEN 'week'  THEN make_interval(weeks  => p_value)
        WHEN 'month' THEN make_interval(months => p_value)
    END;
$$ LANGUAGE sql IMMUTABLE;


-- ===========================================================================
-- 7. MEMBERS
-- ===========================================================================
-- Personal data only. Plan / dates / money live in `memberships` so a member
-- can renew, upgrade or lapse without losing history.
-- user_id stays NULL for the whole of V1 — populating it is all the member
-- portal will need.

CREATE TABLE members (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id           bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    branch_id           bigint      NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
    user_id             bigint      UNIQUE REFERENCES users(id) ON DELETE SET NULL,
    member_code         text        NOT NULL,       -- 'MEM-2023-0142'
    full_name           text        NOT NULL,
    phone               text        NOT NULL,
    email               citext,
    gender              text        CHECK (gender IN ('male','female','other','undisclosed')),
    date_of_birth       date,
    address             text,
    photo_url           text,
    emergency_contact_name  text,
    emergency_contact_phone text,
    joined_on           date        NOT NULL DEFAULT CURRENT_DATE,
    status              text        NOT NULL DEFAULT 'active'
                        CHECK (status IN ('active','inactive','frozen','banned')),
    -- DPDP: marketing consent. A renewal reminder is a service message about a
    -- contract the member signed and needs none; an offer is marketing and
    -- does. Default false, because opt-in means opt-in. See 009_messaging.sql.
    marketing_opt_in    boolean     NOT NULL DEFAULT false,
    notes               text,
    created_by_user_id  bigint      REFERENCES users(id) ON DELETE SET NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    deleted_at          timestamptz,
    UNIQUE (tenant_id, member_code)
);
CREATE TRIGGER trg_members_updated BEFORE UPDATE ON members
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE INDEX idx_members_name_trgm ON members USING gin (full_name gin_trgm_ops);
CREATE INDEX idx_members_branch    ON members(tenant_id, branch_id, status)
    WHERE deleted_at IS NULL;
CREATE INDEX idx_members_phone     ON members(tenant_id, phone);


-- ===========================================================================
-- 8. MEMBERSHIPS  (one row per plan purchase — renewals are new rows)
-- ===========================================================================
CREATE TABLE memberships (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id           bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    branch_id           bigint      NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
    member_id           bigint      NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    plan_id             bigint      NOT NULL REFERENCES membership_plans(id),

    start_date          date        NOT NULL,
    end_date            date        NOT NULL,       -- "calculated automatically"
    -- price snapshot: the plan's price may change later, receipts must not
    plan_price          numeric(10,2) NOT NULL CHECK (plan_price >= 0),
    discount_amount     numeric(10,2) NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
    total_amount        numeric(10,2) NOT NULL
                        GENERATED ALWAYS AS (plan_price - discount_amount) STORED,

    status              text        NOT NULL DEFAULT 'active'
                        CHECK (status IN ('active','expired','cancelled','frozen','upcoming')),
    assigned_trainer_id bigint      REFERENCES staff(id) ON DELETE SET NULL,
    created_by_user_id  bigint      REFERENCES users(id) ON DELETE SET NULL,
    cancelled_at        timestamptz,
    cancellation_reason text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CHECK (end_date >= start_date)
);
CREATE TRIGGER trg_memberships_updated BEFORE UPDATE ON memberships
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- no two overlapping active memberships for one member
CREATE UNIQUE INDEX idx_one_active_membership
    ON memberships(member_id) WHERE status = 'active';
-- "Expiring Soon" card + the nightly expiry job
CREATE INDEX idx_memberships_expiry
    ON memberships(tenant_id, branch_id, end_date) WHERE status = 'active';
CREATE INDEX idx_memberships_member ON memberships(member_id, start_date DESC);


-- ===========================================================================
-- 9. PAYMENTS
-- ===========================================================================
-- One row per transaction, NOT per membership. The Add Member screen shows
-- Price / Paid / Remaining — only the first is stored (on memberships).
-- Paid and Remaining are always derived, so they can never drift apart.
-- Partial payments and installments then work with no extra design.

CREATE TABLE payments (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id           bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    branch_id           bigint      NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
    receipt_no          text,
    member_id           bigint      NOT NULL REFERENCES members(id) ON DELETE RESTRICT,
    membership_id       bigint      REFERENCES memberships(id) ON DELETE SET NULL,
    purpose             text        NOT NULL DEFAULT 'membership'
                        CHECK (purpose IN ('membership','personal_training','registration','merchandise','other')),
    amount              numeric(10,2) NOT NULL CHECK (amount > 0),
    method              text        NOT NULL
                        CHECK (method IN ('cash','card','bank_transfer','upi','other')),
    paid_at             timestamptz NOT NULL DEFAULT now(),
    reference_no        text,                       -- UPI ref / txn id / cheque no
    status              text        NOT NULL DEFAULT 'completed'
                        CHECK (status IN ('completed','pending','failed','refunded')),
    collected_by_user_id bigint     REFERENCES users(id) ON DELETE SET NULL,
    notes               text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, receipt_no)
);
CREATE TRIGGER trg_payments_updated BEFORE UPDATE ON payments
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE INDEX idx_payments_member     ON payments(member_id, paid_at DESC);
CREATE INDEX idx_payments_membership ON payments(membership_id);
CREATE INDEX idx_payments_revenue    ON payments(tenant_id, branch_id, paid_at DESC)
    WHERE status = 'completed';


-- ===========================================================================
-- 10. MEMBER CHECK-INS  ("Check-in Member" / QR scanner)
-- ===========================================================================
-- Deliberately NOT the same table as staff_attendance: a member can visit
-- several times a day and has no shift, so the shape differs.
-- branch_id here is the one field that is genuinely unrecoverable later —
-- you cannot reconstruct which location someone walked into.

CREATE TABLE member_checkins (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id           bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    branch_id           bigint      NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
    member_id           bigint      NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    membership_id       bigint      REFERENCES memberships(id) ON DELETE SET NULL,
    checked_in_at       timestamptz NOT NULL DEFAULT now(),
    checked_out_at      timestamptz,
    method              text        NOT NULL DEFAULT 'manual'
                        CHECK (method IN ('manual','qr','biometric','rfid','mobile')),
    recorded_by_user_id bigint      REFERENCES users(id) ON DELETE SET NULL,
    CHECK (checked_out_at IS NULL OR checked_out_at >= checked_in_at)
);
CREATE INDEX idx_checkins_member_time ON member_checkins(member_id, checked_in_at DESC);
-- Query "today" with a range predicate, not a cast:
--   WHERE checked_in_at >= :day_start AND checked_in_at < :day_start + interval '1 day'
-- (an expression index on AT TIME ZONE won't build — that function is STABLE)
CREATE INDEX idx_checkins_branch_time
    ON member_checkins(tenant_id, branch_id, checked_in_at DESC);


-- ===========================================================================
-- 11. PERSONAL TRAINING  ("14 Active PT Clients" / "View Client Roster")
-- ===========================================================================
CREATE TABLE pt_assignments (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    trainer_id      bigint      NOT NULL REFERENCES staff(id) ON DELETE RESTRICT,
    member_id       bigint      NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    start_date      date        NOT NULL DEFAULT CURRENT_DATE,
    end_date        date,
    sessions_total  integer     CHECK (sessions_total > 0),
    sessions_used   integer     NOT NULL DEFAULT 0 CHECK (sessions_used >= 0),
    fee             numeric(10,2) CHECK (fee >= 0),
    status          text        NOT NULL DEFAULT 'active'
                    CHECK (status IN ('active','completed','cancelled')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CHECK (end_date IS NULL OR end_date >= start_date)
);
CREATE TRIGGER trg_pt_updated BEFORE UPDATE ON pt_assignments
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE UNIQUE INDEX idx_one_active_pt
    ON pt_assignments(member_id) WHERE status = 'active';
CREATE INDEX idx_pt_trainer ON pt_assignments(trainer_id) WHERE status = 'active';


-- ===========================================================================
-- 12. ACTIVITY LOG  (dashboard "Recent Activity")
-- ===========================================================================
CREATE TABLE activity_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    branch_id       bigint      REFERENCES branches(id) ON DELETE SET NULL,
    actor_user_id   bigint      REFERENCES users(id) ON DELETE SET NULL,
    action          text        NOT NULL,   -- 'member.created', 'payment.recorded'
    entity_type     text        NOT NULL,   -- 'member','payment','staff'
    entity_id       bigint,
    description     text        NOT NULL,   -- 'Sarah Jenkins joined Basic Monthly plan.'
    metadata        jsonb       NOT NULL DEFAULT '{}',
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_activity_recent ON activity_log(tenant_id, created_at DESC);
CREATE INDEX idx_activity_entity ON activity_log(entity_type, entity_id);


-- ===========================================================================
-- VIEWS — the derived numbers the screens need
-- ===========================================================================

-- Price / Paid / Remaining for the member form and the payments screen
CREATE VIEW v_membership_balance AS
SELECT
    m.id            AS membership_id,
    m.tenant_id,
    m.member_id,
    m.total_amount,
    COALESCE(SUM(p.amount) FILTER (WHERE p.status = 'completed'), 0) AS paid_amount,
    m.total_amount
      - COALESCE(SUM(p.amount) FILTER (WHERE p.status = 'completed'), 0) AS balance_amount,
    CASE
        WHEN COALESCE(SUM(p.amount) FILTER (WHERE p.status = 'completed'), 0) >= m.total_amount
             THEN 'paid'
        WHEN COALESCE(SUM(p.amount) FILTER (WHERE p.status = 'completed'), 0) > 0
             THEN 'partial'
        ELSE 'pending'
    END AS payment_status
FROM memberships m
LEFT JOIN payments p ON p.membership_id = m.id
GROUP BY m.id;

-- Members list + "Expiring Soon" dashboard card
CREATE VIEW v_member_overview AS
SELECT
    mem.id,
    mem.tenant_id,
    mem.branch_id,
    mem.member_code,
    mem.full_name,
    mem.phone,
    mem.status              AS member_status,
    ms.id                   AS membership_id,
    mp.name                 AS plan_name,
    ms.start_date,
    ms.end_date,
    (ms.end_date - CURRENT_DATE) AS days_remaining,
    CASE
        WHEN ms.id IS NULL                   THEN 'no_membership'
        WHEN ms.end_date <  CURRENT_DATE     THEN 'expired'
        WHEN ms.end_date <= CURRENT_DATE + 7 THEN 'expiring_soon'
        ELSE 'active'
    END                     AS membership_state,
    b.paid_amount,
    b.balance_amount,
    b.payment_status
FROM members mem
LEFT JOIN LATERAL (
    SELECT * FROM memberships
    WHERE member_id = mem.id AND status IN ('active','expired')
    ORDER BY end_date DESC LIMIT 1
) ms ON true
LEFT JOIN membership_plans mp    ON mp.id = ms.plan_id
LEFT JOIN v_membership_balance b ON b.membership_id = ms.id
WHERE mem.deleted_at IS NULL;

-- Attendance page: monthly present/absent/% per staff member
CREATE VIEW v_staff_attendance_monthly AS
SELECT
    s.id            AS staff_id,
    s.tenant_id,
    s.branch_id,
    s.staff_code,
    s.full_name,
    s.role,
    date_trunc('month', a.attendance_date)::date AS month,
    COUNT(*) FILTER (WHERE a.status IN ('present','late','half_day'))  AS days_present,
    COUNT(*) FILTER (WHERE a.status = 'absent')                        AS days_absent,
    COUNT(*) FILTER (WHERE a.status = 'leave')                         AS days_leave,
    COUNT(*) FILTER (WHERE a.status NOT IN ('holiday','week_off'))     AS working_days,
    ROUND(
        100.0 * COUNT(*) FILTER (WHERE a.status IN ('present','late','half_day'))
        / NULLIF(COUNT(*) FILTER (WHERE a.status NOT IN ('holiday','week_off')), 0)
    , 1) AS attendance_pct
FROM staff s
JOIN staff_attendance a ON a.staff_id = s.id
WHERE s.deleted_at IS NULL
GROUP BY s.id, date_trunc('month', a.attendance_date);


-- ===========================================================================
-- SEED — the single tenant + branch V1 runs against
-- ===========================================================================
INSERT INTO tenants (name, slug, tagline)
VALUES ('Steel Flex', 'steelflex', 'Elite Performance Training');

INSERT INTO branches (tenant_id, name, code, currency, timezone, country)
VALUES (1, 'Main Branch', 'BR-01', 'INR', 'Asia/Kolkata', 'IN');

INSERT INTO shifts (tenant_id, name, start_time, end_time) VALUES
    (1, 'Morning', '06:00', '14:00'),
    (1, 'Mid-day', '10:00', '18:00'),
    (1, 'Evening', '14:00', '22:00'),
    (1, 'Flex',    NULL,    NULL);

INSERT INTO tags (tenant_id, category, name) VALUES
    (1, 'responsibility', 'Check-in'),
    (1, 'responsibility', 'Tours'),
    (1, 'responsibility', 'Inventory'),
    (1, 'responsibility', 'Scheduling'),
    (1, 'responsibility', 'Lead'),
    (1, 'specialization', 'Strength & Conditioning'),
    (1, 'specialization', 'Yoga & Mobility'),
    (1, 'specialization', 'CrossFit L3'),
    (1, 'specialization', 'Nutritionist');


-- ===========================================================================
-- DEFERRED TO V2 — all addable with zero migration risk
-- ===========================================================================
-- These are standalone tables or nullable columns. None of them force a
-- change to anything above, which is why they are safe to leave out now.
--
--   membership_freezes   pause/hold a membership
--   notifications        the bell icon (no page is designed for it yet)
--   refresh_tokens       DECIDED: JWT + rotating refresh tokens.
--                        See 002_refresh_tokens.sql — apply it with this file.
--   member portal        populate members.user_id, role 'member' already valid
--   biometric_devices    device registry once you move off manual marking
--   expense / payroll    if the client asks for full financials
-- ===========================================================================
