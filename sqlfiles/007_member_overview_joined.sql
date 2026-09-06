-- ============================================================================
-- MIGRATION 007 — v_member_overview gains joined_on
-- ============================================================================
-- The Members list needs to answer "who joined this month" — the dashboard's
-- New Members card counts it (DashboardService.NewMembersThisMonthAsync) and
-- the card now links to the list behind the number. The list reads the view,
-- and the view has never carried joined_on.
--
-- Appended, never reordered — the same contract 004 states: CREATE OR REPLACE
-- VIEW keeps working only while every existing column holds its name, type and
-- position, so adding to the end is the one edit that is always safe. The 19
-- columns below are byte-identical to 004; only the last line is new.
--
-- No index needed: idx_members_joined (tenant_id, branch_id, joined_on DESC)
-- WHERE deleted_at IS NULL was added in 006 for the dashboard's count, and the
-- list filters on the same predicate.
-- ============================================================================

CREATE OR REPLACE VIEW v_member_overview AS
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
    b.payment_status,
    -- appended in 004 --------------------------------------------------------
    mem.email,
    mem.photo_url,
    ms.plan_id,
    -- appended in 007 --------------------------------------------------------
    mem.joined_on
FROM members mem
LEFT JOIN LATERAL (
    SELECT * FROM memberships
    WHERE member_id = mem.id AND status IN ('active','expired')
    ORDER BY end_date DESC LIMIT 1
) ms ON true
LEFT JOIN membership_plans mp    ON mp.id = ms.plan_id
LEFT JOIN v_membership_balance b ON b.membership_id = ms.id
WHERE mem.deleted_at IS NULL;
