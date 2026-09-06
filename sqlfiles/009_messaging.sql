-- ============================================================================
-- MIGRATION 009 — Messaging: a durable, quota-aware queue for outbound mail
-- ============================================================================
-- Until now the API sent exactly one email — the password-reset link — inline
-- on the request thread, with the failure swallowed so the uniform 202 that
-- closes the account-enumeration oracle would survive a provider outage. That
-- swallow is correct and it is also the whole problem: with a real Brevo key
-- behind it, a blip silently drops the message while the caller is told one is
-- on its way.
--
-- This module is the queue that fixes it, plus the accounting the free tier
-- forces on us. Five things live here, and four of them have an obvious
-- implementation that is wrong.
--
-- 1. THE RESERVE IS A BUDGET PARTITION, NOT A SORT ORDER.
--    "Send high priority first" only orders messages that are already queued.
--    It does nothing for a password reset that has not been requested yet: the
--    reminder generator queues 270 renewals at 02:00, the dispatcher finds no
--    high-priority work, and the day's whole allowance is gone by 03:00. At
--    15:00 someone locks themselves out and the queue is beautifully sorted
--    with zero budget behind it.
--
--    So the reserve is a CEILING ON THE NORMAL POOL, applied at claim time:
--
--        high_room   = limit - high_sent
--        normal_room = LEAST(limit - reserve - normal_sent,
--                            limit - high_sent - normal_sent)
--
--    High priority may spend the whole limit — a reset is not something to
--    ration. Normal may never spend past (limit - reserve), however empty the
--    day has been. The second term of the LEAST is the one that is easy to drop
--    and it is the one that matters: if high has already burned 250 of 270,
--    normal's own sub-cap still reads "240 available" while 20 actually remain.
--
-- 2. THE QUOTA DAY IS UTC, BECAUSE THAT IS THE VENDOR'S DAY.
--    Everything else in this schema that says "today" means the branch's day
--    (Asia/Kolkata, via branches.timezone) — see 005_attendance_module.sql.
--    Not this. Brevo's free-tier counter resets at UTC midnight, and the two
--    are 5.5 hours apart. Count in IST and our counter resets at 18:30 UTC into
--    a vendor bucket that already holds a full day's sends; the dispatcher
--    starts a confident fresh 270 and every one of them comes back 429.
--    Budget in the vendor's clock. Convert to the branch's only for display.
--
-- 3. THE COUNTER IS A TABLE, NOT COUNT(*).
--    SELECT count(*) ... WHERE status='sent' AND sent_at >= today is the
--    obvious version and it decays: purge_message_queue() deletes sent rows, so
--    on the 30-day boundary the count silently falls and the module believes it
--    has budget it does not have. message_quota_usage is never purged, and
--    mark_message_sent() increments it in the SAME statement that flips the row
--    to 'sent' — the pair commit together or not at all.
--
-- 4. GENERATION MUST BE IDEMPOTENT, NOT SCHEDULED-ONCE.
--    A nightly timer is wrong the first time the process restarts at 02:05, or
--    is down all night, or runs on two instances. Same answer as
--    close_out_attendance(): make the write idempotent by construction and then
--    run it as often as you like. Every auto-generated message carries a
--    dedupe_key ('renewal:{membership_id}:{days_before}') under a PARTIAL
--    unique index, and the generator inserts ON CONFLICT DO NOTHING.
--    The index is partial because password resets have NO natural key and MUST
--    be repeatable — someone may legitimately ask for three links in ten
--    minutes. Those rows carry dedupe_key IS NULL and the index ignores them.
--
-- 5. EVERY MESSAGE EXPIRES.
--    The owner can switch sending off from the Settings screen. If "off" simply
--    held the queue, flipping it back on would deliver a week of stale mail at
--    once — including reset links whose tokens died an hour after they were
--    minted. expires_at turns that into a non-event: a message past its expiry
--    is marked 'skipped', never sent. One column also covers the server that
--    was down for a week and the backlog thunder when the switch comes back.
--
-- And one security property that is easy to undo:
--
--    THE RENDERED RESET EMAIL CONTAINS A RAW BEARER TOKEN.
--    users.reset_token_hash deliberately stores only a SHA-256 — the raw token
--    is never at rest. A queue holding rendered bodies reintroduces exactly
--    what that hash exists to avoid, so body_html and body_text are set to NULL
--    the instant a row reaches a terminal state. The link lives in Postgres for
--    the seconds between enqueue and delivery; afterwards the row survives as
--    an audit record carrying no secret. Anything that widens that window --
--    a "resend" button reusing a stored body, longer retention of live bodies,
--    an admin screen that renders one -- undoes it. Resend must re-render from
--    the template and mint a fresh token.
-- ============================================================================


-- ---------------------------------------------------------------------------
-- 1. The queue
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS message_queue (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id)  ON DELETE CASCADE,

    -- Nullable: a password reset belongs to an account, not to a branch.
    branch_id       bigint      REFERENCES branches(id) ON DELETE SET NULL,

    -- The seam for SMS. One queue, one set of quota/retry/dedupe/expiry rules;
    -- a second channel is a sender implementation and a quota row, not a
    -- parallel copy of all of this that drifts out of step within a month.
    channel         text        NOT NULL DEFAULT 'email'
                    CHECK (channel IN ('email','sms')),

    -- Derived from `purpose` by the application, never taken from a caller: a
    -- caller who could ask for 'high' could spend the password-reset reserve.
    priority        text        NOT NULL
                    CHECK (priority IN ('high','normal')),

    -- Renewals and offers share a priority and nothing else. A renewal reminder
    -- is a service message about a contract the member signed; an offer is
    -- marketing and needs consent (members.marketing_opt_in). Separate values
    -- so the gate can differ.
    purpose         text        NOT NULL
                    CHECK (purpose IN ('password_reset','password_changed',
                                       'renewal_reminder','offer')),

    -- Exactly one addressee. Member-directed mail obeys the active-member rule;
    -- user-directed mail (password reset) cannot, because owners, admins,
    -- receptionists and trainers have no member row at all — gating resets on
    -- member status locks the owner out of their own system with no way back.
    member_id       bigint      REFERENCES members(id) ON DELETE CASCADE,
    user_id         bigint      REFERENCES users(id)   ON DELETE CASCADE,

    -- Snapshot at enqueue; re-validated against the live row before sending.
    to_address      citext      NOT NULL,

    subject         text,                       -- NULL for SMS
    body_html       text,                       -- NULL once terminal
    body_text       text,                       -- NULL once terminal

    dedupe_key      text,                       -- NULL = repeatable

    status          text        NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending','sending','sent','failed','skipped')),

    skip_reason     text        CHECK (skip_reason IN
                        ('expired','member_inactive','no_consent','no_address','disabled')),

    attempts        int         NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    last_error      text,
    provider_message_id text,

    claimed_at      timestamptz,
    sent_at         timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT message_queue_one_addressee
        CHECK ((member_id IS NULL) <> (user_id IS NULL)),

    -- A terminal row keeps no rendered body. Enforced rather than remembered:
    -- this is the constraint that stops a raw reset link surviving in the
    -- database, and it fails loudly if a future code path forgets to clear it.
    CONSTRAINT message_queue_terminal_has_no_body
        CHECK (status IN ('pending','sending')
               OR (body_html IS NULL AND body_text IS NULL))
);

-- Idempotent generation (§4). Partial: repeatable mail carries no key.
CREATE UNIQUE INDEX IF NOT EXISTS idx_message_dedupe
    ON message_queue(dedupe_key) WHERE dedupe_key IS NOT NULL;

-- The drain query. Partial on 'pending' so the index stays the size of the
-- backlog rather than the size of the history.
CREATE INDEX IF NOT EXISTS idx_message_drain
    ON message_queue(channel, priority, next_attempt_at) WHERE status = 'pending';

-- The stranded-row rescue in claim_message_batch().
CREATE INDEX IF NOT EXISTS idx_message_claimed
    ON message_queue(claimed_at) WHERE status = 'sending';

-- The Settings screen's queue table, and the retention purge.
CREATE INDEX IF NOT EXISTS idx_message_history
    ON message_queue(tenant_id, created_at DESC);


-- ---------------------------------------------------------------------------
-- 2. The daily counter  (§2, §3)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS message_quota_usage (
    quota_date  date        NOT NULL,   -- UTC: the vendor's day, not the gym's
    channel     text        NOT NULL CHECK (channel IN ('email','sms')),
    high_sent   int         NOT NULL DEFAULT 0 CHECK (high_sent   >= 0),
    normal_sent int         NOT NULL DEFAULT 0 CHECK (normal_sent >= 0),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (quota_date, channel)
);
-- Deliberately never purged. 365 rows a year, and it is the only thing that
-- can answer "did we hit the ceiling last Tuesday" after the queue has aged out.


-- ---------------------------------------------------------------------------
-- 3. Application settings
-- ---------------------------------------------------------------------------
-- NOT part of site_settings. That table is the public website's CMS content;
-- this is operational configuration. Merging them would put a marketing
-- screen's Save button in the path of the gym's transactional mail.
--
-- The limits live in the database rather than appsettings.json because the
-- owner must be able to raise them the day the gym upgrades its Brevo plan,
-- without a redeploy. appsettings.json holds only the seed defaults.
CREATE TABLE IF NOT EXISTS app_settings (
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id             bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,

    -- The master switch the owner asked for. Off = no member mail of any kind.
    emails_enabled        boolean     NOT NULL DEFAULT true,

    -- Read literally, "no one gets email" also locks every user out of password
    -- recovery — including the owner who flipped the switch. So it is two
    -- booleans. While this is false, reset and password-changed mail still
    -- flows with the master off; ticking it is the genuinely-everything option,
    -- and the UI says what that costs.
    suspend_transactional boolean     NOT NULL DEFAULT false,

    daily_email_limit     int         NOT NULL DEFAULT 270 CHECK (daily_email_limit > 0),
    high_priority_reserve int         NOT NULL DEFAULT 30  CHECK (high_priority_reserve >= 0),

    -- Days before expiry to remind. A list, not a scalar: 7/3/1 is then a
    -- settings change rather than a code change, and the dedupe key carries the
    -- offset so the three reminders never collide.
    reminder_days_before  int[]       NOT NULL DEFAULT '{3}',

    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),

    UNIQUE (tenant_id),
    CONSTRAINT app_settings_reserve_fits CHECK (high_priority_reserve < daily_email_limit)
);

CREATE TRIGGER trg_app_settings_updated BEFORE UPDATE ON app_settings
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Seed a row per existing tenant so the first read never has to write.
INSERT INTO app_settings (tenant_id)
SELECT id FROM tenants
ON CONFLICT (tenant_id) DO NOTHING;


-- ---------------------------------------------------------------------------
-- 4. Marketing consent on members
-- ---------------------------------------------------------------------------
-- DPDP: a renewal reminder is a service message about a contract the member
-- signed and needs no separate consent. An offer is marketing and does. Default
-- false, because opt-in means opt-in — and the same reason site_transformations
-- gates publication on an explicit, server-stamped consent.
--
-- Mirrored into schema_v1.sql, which stays the source of truth for a fresh
-- install; this file brings existing databases into line.
ALTER TABLE members
    ADD COLUMN IF NOT EXISTS marketing_opt_in boolean NOT NULL DEFAULT false;


-- ---------------------------------------------------------------------------
-- 5. claim_message_batch — the whole of §1, §2 and the concurrency rules
-- ---------------------------------------------------------------------------
-- Expressed here rather than in LINQ for the same reason close_out_attendance()
-- is: the arithmetic is fiddly, and it has to be atomic against a second API
-- instance running the identical tick. FOR UPDATE SKIP LOCKED is what gives
-- that — two workers claim disjoint sets and neither blocks.
CREATE OR REPLACE FUNCTION claim_message_batch(
    p_channel     text,
    p_daily_limit int,
    p_reserve     int,
    p_batch       int,
    p_high_only   boolean DEFAULT false
)
RETURNS SETOF message_queue
LANGUAGE plpgsql
AS $$
DECLARE
    v_day         date := (now() AT TIME ZONE 'UTC')::date;
    v_high_sent   int;
    v_normal_sent int;
    v_high_room   int;
    v_normal_room int;
    v_high_ids    bigint[];
    v_normal_ids  bigint[];
BEGIN
    -- Rescue rows stranded mid-send. A process killed between the claim and the
    -- provider call would otherwise hold a message forever. Ten minutes is far
    -- longer than any send, and re-sending a reminder is strictly better than
    -- losing a reset link.
    UPDATE message_queue
       SET status = 'pending', claimed_at = NULL
     WHERE status = 'sending'
       AND claimed_at < now() - interval '10 minutes';

    -- Read today's usage. The row must exist before we can read it, and this is
    -- also what makes the first tick of a new UTC day start from zero.
    INSERT INTO message_quota_usage (quota_date, channel)
    VALUES (v_day, p_channel)
    ON CONFLICT (quota_date, channel) DO NOTHING;

    SELECT high_sent, normal_sent
      INTO v_high_sent, v_normal_sent
      FROM message_quota_usage
     WHERE quota_date = v_day AND channel = p_channel;

    -- §1. Two ceilings. High may spend the whole limit; normal may not cross
    -- (limit - reserve), NOR the global limit once high has eaten into it.
    v_high_room := GREATEST(p_daily_limit - v_high_sent, 0);
    v_normal_room := GREATEST(
        LEAST(p_daily_limit - p_reserve - v_normal_sent,
              p_daily_limit - v_high_sent - v_normal_sent), 0);

    -- The master switch is off but transactional mail is still allowed: the
    -- worker asks for high priority only rather than filtering afterwards, so
    -- normal rows are never even claimed and cannot be stranded in 'sending'.
    IF p_high_only THEN
        v_normal_room := 0;
    END IF;

    SELECT array_agg(id) INTO v_high_ids FROM (
        SELECT id FROM message_queue
         WHERE status = 'pending' AND channel = p_channel AND priority = 'high'
           AND next_attempt_at <= now()
         ORDER BY next_attempt_at, id
         LIMIT LEAST(v_high_room, p_batch)
         FOR UPDATE SKIP LOCKED
    ) h;

    SELECT array_agg(id) INTO v_normal_ids FROM (
        SELECT id FROM message_queue
         WHERE status = 'pending' AND channel = p_channel AND priority = 'normal'
           AND next_attempt_at <= now()
         ORDER BY next_attempt_at, id
         LIMIT GREATEST(LEAST(v_normal_room,
                              p_batch - COALESCE(array_length(v_high_ids, 1), 0)), 0)
         FOR UPDATE SKIP LOCKED
    ) n;

    RETURN QUERY
    UPDATE message_queue m
       SET status = 'sending', claimed_at = now()
     WHERE m.id = ANY (COALESCE(v_high_ids, '{}'::bigint[])
                    || COALESCE(v_normal_ids, '{}'::bigint[]))
    RETURNING m.*;
END;
$$;


-- ---------------------------------------------------------------------------
-- 6. mark_message_sent — the atomic pair from §3
-- ---------------------------------------------------------------------------
-- The flip to 'sent', the clearing of the body, and the increment of the day's
-- counter are one statement. A crash cannot land between them, so the counter
-- can never over-report what was actually delivered. The one remaining failure
-- mode is the other direction (Brevo accepted it, the transaction then rolled
-- back) which UNDER-counts by one — the safe direction: it spends less than the
-- budget, never more.
CREATE OR REPLACE FUNCTION mark_message_sent(p_id bigint, p_provider_id text)
RETURNS boolean
LANGUAGE plpgsql
AS $$
DECLARE
    v_priority text;
    v_channel  text;
    v_day      date := (now() AT TIME ZONE 'UTC')::date;
BEGIN
    UPDATE message_queue
       SET status = 'sent',
           sent_at = now(),
           provider_message_id = p_provider_id,
           last_error = NULL,
           body_html = NULL,          -- §"raw bearer token": terminal keeps no body
           body_text = NULL
     WHERE id = p_id AND status = 'sending'
    RETURNING priority, channel INTO v_priority, v_channel;

    IF v_priority IS NULL THEN
        RETURN false;                 -- someone else finished it; do not double-count
    END IF;

    INSERT INTO message_quota_usage (quota_date, channel, high_sent, normal_sent)
    VALUES (v_day, v_channel,
            CASE WHEN v_priority = 'high'   THEN 1 ELSE 0 END,
            CASE WHEN v_priority = 'normal' THEN 1 ELSE 0 END)
    ON CONFLICT (quota_date, channel) DO UPDATE
       SET high_sent   = message_quota_usage.high_sent   + EXCLUDED.high_sent,
           normal_sent = message_quota_usage.normal_sent + EXCLUDED.normal_sent,
           updated_at  = now();

    RETURN true;
END;
$$;


-- ---------------------------------------------------------------------------
-- 7. purge_message_queue — DPDP retention
-- ---------------------------------------------------------------------------
-- An audit trail of who was mailed is not a reason to keep it forever. Bodies
-- are already NULL by the time a row is terminal; this removes the addresses.
-- message_quota_usage is untouched — it holds no personal data and the counter
-- must outlive the rows it counted (§3).
CREATE OR REPLACE FUNCTION purge_message_queue(p_keep_days int DEFAULT 30)
RETURNS integer
LANGUAGE plpgsql
AS $$
DECLARE
    v_deleted integer;
BEGIN
    DELETE FROM message_queue
     WHERE status IN ('sent','failed','skipped')
       AND created_at < now() - make_interval(days => p_keep_days);

    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    RETURN v_deleted;
END;
$$;
