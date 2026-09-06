-- ============================================================================
-- MIGRATION 010 — the Payments module
-- ============================================================================
-- The centralized payments page. There is no new table: `payments` has been
-- the money ledger since schema_v1 and this module is the read model over it,
-- plus a receipt number the application has never written.
--
-- Three things here:
--
--   1. receipt_no is backfilled and made self-generating. UNIQUE (tenant_id,
--      receipt_no) has existed since day one and has never been enforced,
--      because Postgres treats NULLs as distinct and every row is NULL.
--      Receipt No is the first column of the payments table the client asked
--      for; an em dash on every historical row is a broken screen, not a
--      migration deferred.
--
--   2. v_payment_ledger — one row per transaction, joined to the member, the
--      membership, the plan and v_membership_balance.
--
--   3. One unpartial index. idx_payments_revenue is partial on
--      status = 'completed' (schema_v1.sql:430) and therefore cannot serve the
--      All Payments tab, which spans refunded and failed, nor a status filter
--      that selects them.
--
-- APPEND-ONLY from here. v_payment_ledger is created with CREATE OR REPLACE so
-- 011 can extend it, and that only works while every existing column keeps its
-- name, type and position. Add to the end; never reorder. The same contract
-- 004 and 007 state for v_member_overview.
-- ============================================================================


-- ---------------------------------------------------------------------------
-- 1. Receipt numbers
-- ---------------------------------------------------------------------------
-- A sequence with a column DEFAULT, rather than the NextMemberCodeAsync
-- pattern (MemberService.cs:890) that reads every existing code into memory
-- and retries on a unique violation. Members are created a few times a day;
-- payments are written on every instalment and every renewal, so the racy
-- read-max-then-insert shape is the wrong one here. nextval() is atomic, needs
-- no retry loop, and — because the DEFAULT lives on the column — needs no
-- application code at all. EF only has to stop sending the column.
--
-- The sequence is GLOBAL, not per tenant. That satisfies
-- UNIQUE (tenant_id, receipt_no) trivially and is correct for V1's single
-- tenant. Tenant #2 wants a series that restarts per tenant — a sequence each,
-- or a numbering table — which is the same note Tenancy.cs already carries
-- about what has to change when a second tenant arrives.

CREATE SEQUENCE IF NOT EXISTS payment_receipt_seq AS bigint START 1;

-- Numbered in payment chronology, so the series reads the way a receipt book
-- does rather than in insert order. Idempotent: WHERE receipt_no IS NULL means
-- a re-run touches nothing.
WITH numbered AS (
    SELECT id, row_number() OVER (ORDER BY paid_at, id) AS n
      FROM payments
     WHERE receipt_no IS NULL
)
UPDATE payments p
   SET receipt_no = 'RCP-' || lpad(n.n::text, 6, '0')
  FROM numbered n
 WHERE p.id = n.id;

-- Park the sequence above whatever has been issued. The third argument is
-- is_called: false on an empty ledger so the first receipt is RCP-000001, true
-- otherwise so the next one is max + 1. Re-running recomputes the same value,
-- and running this file after the API has already been issuing receipts cannot
-- collide with them.
SELECT setval(
    'payment_receipt_seq',
    COALESCE((SELECT MAX(substring(receipt_no FROM 5)::bigint)
                FROM payments
               WHERE receipt_no ~ '^RCP-[0-9]+$'), 1),
    EXISTS (SELECT 1 FROM payments WHERE receipt_no ~ '^RCP-[0-9]+$'));

ALTER TABLE payments
    ALTER COLUMN receipt_no
    SET DEFAULT 'RCP-' || lpad(nextval('payment_receipt_seq')::text, 6, '0');


-- ---------------------------------------------------------------------------
-- 2. v_payment_ledger — the payments page's read model
-- ---------------------------------------------------------------------------
-- Grain is one row per payment. It composes v_membership_balance rather than
-- re-summing payments itself, so a Balance Due on the payments page can never
-- disagree with the same member's Balance Due on the members list — the rule
-- v_member_overview already exists to enforce.
--
-- Three things about this view that are easy to "fix" into a bug:
--
--  * It does NOT filter mem.deleted_at IS NULL, and that is the one place this
--    codebase departs from v_member_overview. MemberService.EraseAsync
--    overwrites full_name to 'Deleted member' and phone to zeroes while
--    deliberately preserving member_code — its own comment calls that "the only
--    handle the payments ledger has left, and dropping it would orphan the
--    money". So this join carries no personal data for an erased member, and
--    hiding the rows would make the ledger disagree with the revenue figure
--    above it, which reads `payments` directly. Personal data goes; the
--    accounting record stays. Both hold here.
--
--  * balance_amount is the MEMBERSHIP's outstanding balance, repeated on every
--    instalment row against that membership. It is not "what was left after
--    this payment". A per-payment running total would need a stored balance,
--    which schema_v1.sql:399 forbids by name: paid and remaining are always
--    derived, so they can never drift apart.
--
--  * membership_id is nullable — ON DELETE SET NULL, and purposes like
--    merchandise or registration have no membership at all — so plan, expiry
--    and balance are LEFT JOINed and come back NULL for those rows. The screen
--    renders an em dash rather than a zero.

CREATE OR REPLACE VIEW v_payment_ledger AS
SELECT
    p.id,
    p.tenant_id,
    p.branch_id,
    p.receipt_no,
    p.member_id,
    mem.member_code,
    mem.full_name           AS member_name,
    mem.phone,
    p.membership_id,
    ms.plan_id,
    mp.name                 AS plan_name,
    ms.end_date,
    p.paid_at,
    p.amount,
    b.balance_amount,
    b.payment_status,
    p.method,
    p.status,
    p.purpose,
    p.reference_no
FROM payments p
JOIN members mem                 ON mem.id = p.member_id
LEFT JOIN memberships ms         ON ms.id = p.membership_id
LEFT JOIN membership_plans mp    ON mp.id = ms.plan_id
LEFT JOIN v_membership_balance b ON b.membership_id = p.membership_id;


-- ---------------------------------------------------------------------------
-- 3. The index the ledger scans
-- ---------------------------------------------------------------------------
-- Same columns as idx_payments_revenue, without its partial predicate. Every
-- tab orders by paid_at DESC within the branch, and This Month, Previous and
-- the custom range are all half-open instant ranges on that same column, so
-- this is the one index the filter surface needs.
--
-- What is deliberately NOT indexed: balance_amount (the Balance Due Only
-- filter) is an aggregate v_membership_balance builds per query, exactly as
-- MemberService.ListAsync already notes for paymentStatus. Plan and member
-- filtering ride the memberships primary key and idx_payments_member.

CREATE INDEX IF NOT EXISTS idx_payments_ledger
    ON payments(tenant_id, branch_id, paid_at DESC);
