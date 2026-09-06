-- ============================================================================
-- MIGRATION 006 — indexes the Dashboard reads through
-- ============================================================================
-- The dashboard is a read-only composition over relations that already exist:
-- v_member_overview for the membership-state cards and the expiring table,
-- payments for monthly revenue, staff_attendance (through the attendance
-- module) for today's roster, and activity_log for the feed. No new table, no
-- view change, nothing to roll back.
--
-- Two of its four queries already have the index they want:
--   idx_payments_revenue  (tenant_id, branch_id, paid_at DESC) WHERE completed
--   idx_activity_recent   (tenant_id, created_at DESC)
-- both written in schema_v1.sql for exactly this screen and unused until now.
--
-- The third does not. "New Members this month" filters members by joined_on
-- within a branch, and nothing indexes that column — the only member indexes
-- are on member_code, phone and the full-text name search. On a few hundred
-- rows a sequential scan is free; the index costs nothing to add now and stops
-- the dashboard degrading as the gym grows.
--
-- Partial on deleted_at IS NULL to match the view and the EF global query
-- filter: an erased member is not a new member.
-- ============================================================================

CREATE INDEX IF NOT EXISTS idx_members_joined
    ON members(tenant_id, branch_id, joined_on DESC)
    WHERE deleted_at IS NULL;
