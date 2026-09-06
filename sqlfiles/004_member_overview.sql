-- ============================================================================
-- MIGRATION 004 — v_member_overview gains email and photo_url
-- ============================================================================
-- The Members list draws an avatar and an email under each name (see
-- `member designs/code 2.html`, the "Member" and "Contact" columns), but
-- v_member_overview in schema_v1.sql selects only full_name and phone.
--
-- The two columns are appended, never reordered: CREATE OR REPLACE VIEW keeps
-- working only while every existing column holds its name, type and position.
-- Adding to the end is the one edit that is always safe.
--
-- Everything else about the view is unchanged, and it is still the single
-- place the "latest membership" LATERAL and the membership_state derivation
-- are expressed — the API reads this rather than reimplementing it in LINQ.
-- It also repairs one column that had drifted from schema_v1.sql: the live
-- `members.emergency_contact_phone` was bigint. A phone number is not a
-- quantity — bigint silently eats a leading zero, refuses '+91 ...' and cannot
-- hold the 10-digit-string contract the API validates against. schema_v1.sql
-- has always said text; this makes the database agree.
-- ============================================================================

-- Idempotent: does nothing once the column is already text.
ALTER TABLE members
    ALTER COLUMN emergency_contact_phone TYPE text
    USING emergency_contact_phone::text;

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
    ms.plan_id
FROM members mem
LEFT JOIN LATERAL (
    SELECT * FROM memberships
    WHERE member_id = mem.id AND status IN ('active','expired')
    ORDER BY end_date DESC LIMIT 1
) ms ON true
LEFT JOIN membership_plans mp    ON mp.id = ms.plan_id
LEFT JOIN v_membership_balance b ON b.membership_id = ms.id
WHERE mem.deleted_at IS NULL;
