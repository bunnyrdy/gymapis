-- ============================================================================
-- MIGRATION 005 — Attendance module: a 'system' actor and the end-of-day close
-- ============================================================================
-- The attendance table itself needs no change. schema_v1.sql already ships
-- `staff_attendance` (one row per staff per day, UNIQUE (staff_id,
-- attendance_date), both indexes and the updated_at trigger) and the
-- `v_staff_attendance_monthly` view that feeds the detail screen's four cards.
-- Only two things were missing for the module to work.
--
-- 1. A way to say "nobody marked this".
--    `marked_via` allowed manual | biometric | qr | mobile — every value names
--    a human at a device. The spec requires that an unmarked day is closed as
--    Absent automatically, and those rows have no actor: marked_by_user_id is
--    NULL. Recording them as 'manual' would be a lie the audit trail could
--    never untangle, so 'system' is added and the API writes it for exactly
--    those rows. The same edit is mirrored into schema_v1.sql, which stays the
--    source of truth for a fresh install; this file brings existing databases
--    into line.
--
-- 2. Somewhere for the end-of-day arithmetic to live.
--    "Automatically mark Absent according to the shift timings" is not a
--    per-day global cutoff. It depends on which shift the person was rostered
--    on *that* date, whether that date was one of their working days, and what
--    time their shift ended — which for the Evening shift is midnight, i.e. the
--    following calendar day. Four rules the naive version gets wrong:
--
--      * The roster is dated. staff_shift_assignments is closed-and-reopened on
--        change, and on the changeover day two rows match the date, so the
--        lookup must tie-break (effective_from DESC, id DESC).
--      * working_days is ISO 1=Mon..7=Sun. Postgres EXTRACT(dow) is 0=Sun, so
--        it must be isodow or Sunday silently becomes Monday.
--      * Flex has NULL start/end times. A shift with no end has no end-of-day,
--        so a Flex member is never auto-absented — the desk marks them.
--      * "Now" is branch-local. branches.timezone is Asia/Kolkata; comparing
--        against UTC would close the day five and a half hours early and stamp
--        late-evening marks onto the wrong date.
--
--    Expressing that in LINQ would scatter it across the service. It goes here
--    instead, the same way v_member_overview owns the members list's
--    derivation, and the API calls it. One statement, idempotent by
--    construction: ON CONFLICT on the existing (staff_id, attendance_date)
--    unique key, so running it twice inserts nothing the second time and two
--    servers running it at once cannot double-write.
-- ============================================================================


-- ---------------------------------------------------------------------------
-- 1. marked_via gains 'system'
-- ---------------------------------------------------------------------------
-- The CHECK was written inline in schema_v1.sql, so Postgres named it
-- staff_attendance_marked_via_check. DROP ... IF EXISTS keeps this re-runnable.
ALTER TABLE staff_attendance
    DROP CONSTRAINT IF EXISTS staff_attendance_marked_via_check;

ALTER TABLE staff_attendance
    ADD CONSTRAINT staff_attendance_marked_via_check
    CHECK (marked_via IN ('manual','biometric','qr','mobile','system'));


-- ---------------------------------------------------------------------------
-- 2. close_out_attendance() — the end-of-day sweep
-- ---------------------------------------------------------------------------
-- Inserts an 'absent' row for every working day, up to p_lookback days back,
-- that has passed its shift end and still has no record. Returns how many rows
-- it wrote, so the caller can log a real number rather than "done".
--
-- p_lookback exists so a server that was down for a week still self-heals on
-- the next call, while a database that has never been swept does not try to
-- backfill a year of history in one statement.
CREATE OR REPLACE FUNCTION close_out_attendance(
    p_tenant   bigint,
    p_branch   bigint,
    p_now      timestamptz DEFAULT now(),
    p_lookback integer     DEFAULT 31
) RETURNS integer
LANGUAGE plpgsql
AS $$
DECLARE
    v_zone     text;
    v_local    timestamp;      -- p_now as wall-clock time at the branch
    v_inserted integer;
BEGIN
    SELECT timezone INTO v_zone
    FROM branches
    WHERE id = p_branch AND tenant_id = p_tenant;

    -- Unknown branch: nothing to close out. Not an error — the caller may be
    -- sweeping a branch that was removed between scheduling and running.
    IF v_zone IS NULL THEN
        RETURN 0;
    END IF;

    v_local := p_now AT TIME ZONE v_zone;

    INSERT INTO staff_attendance (
        tenant_id, branch_id, staff_id, attendance_date, shift_id, status, marked_via
    )
    SELECT s.tenant_id, s.branch_id, s.id, d.day, a.shift_id, 'absent', 'system'
    FROM staff s

    -- Every candidate date: from the later of "p_lookback days ago" and the
    -- day they joined, through today. Nobody is absent before they were hired.
    CROSS JOIN LATERAL (
        SELECT gs::date AS day
        FROM generate_series(
            GREATEST(s.joining_date, v_local::date - p_lookback)::timestamp,
            v_local::date::timestamp,
            interval '1 day'
        ) AS gs
    ) d

    -- The roster row in force on that date. Dated rows, so this is a lookup by
    -- range, and the ORDER BY is the changeover-day tie-break.
    JOIN LATERAL (
        SELECT ssa.shift_id,
               ssa.working_days,
               COALESCE(ssa.custom_start_time, sh.start_time) AS start_time,
               COALESCE(ssa.custom_end_time,   sh.end_time)   AS end_time
        FROM staff_shift_assignments ssa
        JOIN shifts sh ON sh.id = ssa.shift_id
        WHERE ssa.staff_id = s.id
          AND ssa.effective_from <= d.day
          AND (ssa.effective_to IS NULL OR ssa.effective_to >= d.day)
        ORDER BY ssa.effective_from DESC, ssa.id DESC
        LIMIT 1
    ) a ON true

    WHERE s.tenant_id  = p_tenant
      AND s.branch_id  = p_branch
      AND s.deleted_at IS NULL
      -- Only people who are supposed to be turning up. Someone inactive,
      -- on_leave or terminated is not absent, they are not expected.
      AND s.status = 'active'
      -- ISO weekday. A day off the roster is a week off, not an absence.
      AND EXTRACT(isodow FROM d.day)::smallint = ANY (a.working_days)
      -- Flex (NULL end_time) has no end-of-day and is never auto-closed.
      AND a.end_time IS NOT NULL
      -- Has the shift finished? A shift whose end is at or before its start
      -- runs past midnight and finishes on the following day.
      AND v_local > (
            d.day
            + a.end_time
            + CASE
                WHEN a.start_time IS NOT NULL AND a.end_time <= a.start_time
                THEN interval '1 day'
                ELSE interval '0'
              END
          )

    -- The idempotency guarantee. Anything already marked — by a person or by
    -- an earlier sweep — is left exactly as it is.
    ON CONFLICT (staff_id, attendance_date) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END;
$$;


-- ---------------------------------------------------------------------------
-- Notes for whoever extends this
-- ---------------------------------------------------------------------------
-- * The sweep writes 'absent' only. The app never writes any of the other five
--   values the status CHECK still permits (late, half_day, leave, holiday,
--   week_off) — V1 is Present/Absent per the spec, and lateness is carried by
--   the is_late boolean instead of a separate status so that
--   v_staff_attendance_monthly's arithmetic keeps working unchanged.
--
-- * A day that is not in working_days gets no row at all. The API reports it
--   as a week off by reading the roster, which keeps this table meaning
--   "something was expected and this is what happened" rather than filling it
--   with rows for days nobody was due in.
--
-- * staff_attendance's UNIQUE (staff_id, attendance_date) is the only
--   uniqueness in the schema not scoped per-tenant. It is harmless — staff_id
--   already implies a tenant — but if that convention is ever enforced
--   mechanically, this is the row to fix, and this function's ON CONFLICT
--   target would need to move with it.
