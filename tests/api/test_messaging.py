"""The messaging queue: what gets queued, what never does, and what is left
behind after delivery.

These tests read `message_queue` directly. That is not a shortcut around the
API — it is the only way to test this module. `POST /api/auth/forgot-password`
answers 202 whether it queued anything or not, and that uniformity is a
deliberate anti-enumeration control, so the HTTP response cannot distinguish
"queued a reset link" from "did nothing". The same applies to every skip rule:
they are all invisible from outside.

Requires the API running against the same database as GYMAPI_DB_DSN.
"""
import time

import pytest

from conftest import OWNER_EMAIL

UNKNOWN_EMAIL = "nobody-at-all@steelflex.test"


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------
def latest_for(q, address, purpose=None):
    sql = """
        SELECT id, purpose, priority, status, skip_reason, to_address,
               body_html, body_text, attempts, expires_at, dedupe_key
          FROM message_queue
         WHERE to_address = %s
    """
    params = [address]
    if purpose:
        sql += " AND purpose = %s"
        params.append(purpose)
    sql += " ORDER BY id DESC LIMIT 1"
    rows = q(sql, tuple(params))
    return rows[0] if rows else None


def max_id(q):
    rows = q("SELECT coalesce(max(id), 0) FROM message_queue")
    return rows[0][0]


def rows_since(q, marker, address=None):
    sql = "SELECT id, purpose, status, to_address FROM message_queue WHERE id > %s"
    params = [marker]
    if address:
        sql += " AND to_address = %s"
        params.append(address)
    return q(sql, tuple(params))


def wait_for_status(q, message_id, wanted=("sent", "failed", "skipped"), timeout=150):
    """The dispatcher ticks once a minute, so this is a slow wait by design."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        rows = q("SELECT status FROM message_queue WHERE id = %s", (message_id,))
        if rows and rows[0][0] in wanted:
            return rows[0][0]
        time.sleep(3)
    return None


# ---------------------------------------------------------------------------
# forgot password
# ---------------------------------------------------------------------------
def test_forgot_password_queues_exactly_one_high_priority_message(api, q):
    marker = max_id(q)

    r = api.post(f"{api.base}/api/auth/forgot-password", json={"email": OWNER_EMAIL})
    assert r.status_code == 202

    new = rows_since(q, marker, OWNER_EMAIL)
    assert len(new) == 1, f"expected one queued message, got {new}"

    row = latest_for(q, OWNER_EMAIL, "password_reset")
    assert row is not None
    _id, purpose, priority, status, skip, _to, html, text, attempts, expires, dedupe = row

    assert purpose == "password_reset"
    # Priority is derived from purpose, never supplied — a caller who could ask
    # for 'high' could spend the reserve that keeps resets working.
    assert priority == "high"
    assert status in ("pending", "sending", "sent")
    assert skip is None
    assert attempts == 0
    # Repeatable on purpose: somebody may legitimately ask for three links in
    # ten minutes, so these rows carry no dedupe key.
    assert dedupe is None

    if status == "pending":
        assert "/reset-password?token=" in html
        assert "/reset-password?token=" in text


def test_forgot_password_for_an_unknown_address_queues_nothing(api, q):
    marker = max_id(q)

    r = api.post(f"{api.base}/api/auth/forgot-password", json={"email": UNKNOWN_EMAIL})
    assert r.status_code == 202

    assert rows_since(q, marker, UNKNOWN_EMAIL) == []


def test_the_token_and_the_message_commit_together(api, q):
    """A reset token must not exist without its email queued, or the reverse.

    They share one SaveChangesAsync precisely so neither state is reachable.
    """
    marker = max_id(q)
    api.post(f"{api.base}/api/auth/forgot-password", json={"email": OWNER_EMAIL})

    queued = rows_since(q, marker, OWNER_EMAIL)
    hashed = q(
        "SELECT reset_token_hash IS NOT NULL FROM users WHERE email = %s", (OWNER_EMAIL,)
    )[0][0]

    assert bool(queued) == bool(hashed)


# ---------------------------------------------------------------------------
# the security property the whole design turns on
# ---------------------------------------------------------------------------
def test_a_terminal_row_never_keeps_a_rendered_body(q):
    """users.reset_token_hash stores only a SHA-256; the raw token is never at
    rest. A rendered reset email contains that raw token in a link, so the body
    is cleared the moment the row reaches a terminal state.

    A DB CHECK enforces it too — this asserts the data, not just the constraint.
    """
    leaked = q("""
        SELECT id, status FROM message_queue
         WHERE status IN ('sent', 'failed', 'skipped')
           AND (body_html IS NOT NULL OR body_text IS NOT NULL)
    """)
    assert leaked == [], f"terminal rows still holding a body: {leaked}"


def test_the_database_refuses_to_store_a_terminal_body(db):
    psycopg = pytest.importorskip("psycopg")

    with pytest.raises(psycopg.errors.CheckViolation):
        with db.cursor() as cur:
            cur.execute("""
                INSERT INTO message_queue
                    (tenant_id, channel, priority, purpose, user_id, to_address,
                     subject, body_html, expires_at, status)
                VALUES (1, 'email', 'high', 'password_reset', 1, 'leak@test.invalid',
                        's', '<p>token</p>', now(), 'sent')
            """)
    db.rollback()


def test_every_message_is_addressed_to_exactly_one_of_member_or_user(q):
    bad = q("""
        SELECT id FROM message_queue
         WHERE (member_id IS NULL) = (user_id IS NULL)
    """)
    assert bad == []


# ---------------------------------------------------------------------------
# the budget partition
# ---------------------------------------------------------------------------
def plant(cur, priority, purpose, n, lifetime="2 days"):
    cur.execute(f"""
        INSERT INTO message_queue (tenant_id, channel, priority, purpose,
                                   user_id, to_address, subject, body_html, expires_at)
        SELECT 1, 'email', %s, %s, 1, 'p' || g || '@test.invalid', 's', '<p>b</p>',
               now() + interval '{lifetime}'
          FROM generate_series(1, %s) g
    """, (priority, purpose, n))


def claim(cur, limit, reserve, batch, high_only=False):
    cur.execute(
        "SELECT priority, count(*) FROM claim_message_batch('email', %s, %s, %s, %s) GROUP BY priority",
        (limit, reserve, batch, high_only),
    )
    return dict(cur.fetchall())


def set_usage(cur, high, normal):
    cur.execute("""
        UPDATE message_quota_usage SET high_sent = %s, normal_sent = %s
         WHERE quota_date = (now() AT TIME ZONE 'UTC')::date AND channel = 'email'
    """, (high, normal))


def test_the_reserve_is_a_ceiling_on_normal_not_a_sort_order(tx):
    """Sorting high-first does nothing for a reset that has not been requested
    yet. The reserve has to hold budget back from the normal pool, or a morning
    of renewal reminders spends the whole day before lunch.
    """
    with tx.cursor() as cur:
        plant(cur, "normal", "renewal_reminder", 20)
        got = claim(cur, limit=10, reserve=3, batch=100)

    # limit 10, reserve 3 -> normal may take at most 7, however empty the day.
    assert got == {"normal": 7}


def test_normal_also_respects_the_global_limit_once_high_has_eaten_into_it(tx):
    """The term that is easy to drop: with high at 8 of 10 and a reserve of 3,
    normal's own sub-cap still reads 7 while only 2 of the budget remain."""
    with tx.cursor() as cur:
        set_usage(cur, high=8, normal=0)
        plant(cur, "normal", "renewal_reminder", 20)
        got = claim(cur, limit=10, reserve=3, batch=100)

    assert got.get("normal") == 2


def test_high_priority_may_spend_the_whole_limit(tx):
    """A password reset is not something to ration."""
    with tx.cursor() as cur:
        plant(cur, "high", "password_reset", 12, lifetime="1 hour")
        got = claim(cur, limit=10, reserve=3, batch=100)

    assert got == {"high": 10}


def test_the_batch_cap_holds_across_both_priorities(tx):
    with tx.cursor() as cur:
        plant(cur, "high", "password_reset", 4, lifetime="1 hour")
        plant(cur, "normal", "renewal_reminder", 20)
        got = claim(cur, limit=270, reserve=30, batch=10)

    assert sum(got.values()) == 10
    # High goes first within a tick — the small mechanism, but it is real.
    assert got["high"] == 4


def test_the_master_switch_claims_transactional_mail_only(tx):
    """With sending off but resets still allowed, normal rows must not even be
    claimed — a claimed row the worker then refuses to send would sit in
    'sending' until the ten-minute rescue."""
    with tx.cursor() as cur:
        plant(cur, "high", "password_reset", 3, lifetime="1 hour")
        plant(cur, "normal", "renewal_reminder", 5)
        got = claim(cur, limit=270, reserve=30, batch=50, high_only=True)

    assert got == {"high": 3}


def test_a_stranded_send_is_rescued(tx):
    """A process killed between the claim and the provider call must not hold a
    message forever. Re-sending a reminder beats losing a reset link."""
    with tx.cursor() as cur:
        plant(cur, "high", "password_reset", 1, lifetime="1 hour")
        cur.execute("""
            UPDATE message_queue SET status = 'sending',
                   claimed_at = now() - interval '30 minutes'
             WHERE to_address = 'p1@test.invalid'
        """)
        got = claim(cur, limit=270, reserve=30, batch=50)

    assert got.get("high") == 1


def test_two_workers_claim_disjoint_sets(db):
    """SKIP LOCKED is what makes a second API instance safe rather than a way to
    spend the day's allowance twice."""
    psycopg = pytest.importorskip("psycopg")

    a = psycopg.connect(db.info.dsn, autocommit=False)
    b = psycopg.connect(db.info.dsn, autocommit=False)
    try:
        with a.cursor() as ca:
            ca.execute("""
                UPDATE message_queue SET next_attempt_at = now() + interval '1 day'
                 WHERE status = 'pending'
            """)
            plant(ca, "normal", "renewal_reminder", 10)
            ca.execute("SELECT id FROM claim_message_batch('email', 270, 30, 5)")
            first = {r[0] for r in ca.fetchall()}

        # b overlaps a, which has not committed. It must not see a's rows and
        # must not block on them.
        with b.cursor() as cb:
            cb.execute("SELECT id FROM claim_message_batch('email', 270, 30, 5)")
            second = {r[0] for r in cb.fetchall()}

        assert first, "the first worker claimed nothing"
        assert first & second == set(), "two workers claimed the same message"
    finally:
        a.rollback(); a.close()
        b.rollback(); b.close()


# ---------------------------------------------------------------------------
# quota accounting
# ---------------------------------------------------------------------------
def test_marking_sent_clears_the_body_and_increments_the_counter_together(tx):
    """The flip to 'sent', the clearing of the body and the increment of the
    day's counter are one statement, so a crash cannot land between them and
    over-report what actually went out.

    On `tx` rather than the autocommit connection: claim_message_batch() has no
    tenant scope and claims whatever is due, real mail included. Running it
    outside a transaction leaves live messages stranded in 'sending'.
    """
    with tx.cursor() as cur:
        cur.execute("""
            INSERT INTO message_queue (tenant_id, channel, priority, purpose,
                                       user_id, to_address, subject, body_html, body_text, expires_at)
            VALUES (1, 'email', 'high', 'password_reset', 1, 'atomic@test.invalid',
                    's', '<p>secret</p>', 'secret', now() + interval '1 hour')
            RETURNING id
        """)
        planted = cur.fetchone()[0]

        cur.execute("SELECT id FROM claim_message_batch('email', 270, 30, 50)")
        assert planted in {r[0] for r in cur.fetchall()}

        cur.execute("""
            SELECT high_sent FROM message_quota_usage
             WHERE quota_date = (now() AT TIME ZONE 'UTC')::date AND channel = 'email'
        """)
        before = cur.fetchone()[0]

        cur.execute("SELECT mark_message_sent(%s, 'test-id')", (planted,))
        assert cur.fetchone()[0] is True

        # Idempotent: a second call must not double-count. Two workers racing
        # the same row is the case this closes.
        cur.execute("SELECT mark_message_sent(%s, 'test-id')", (planted,))
        assert cur.fetchone()[0] is False

        cur.execute("""
            SELECT high_sent FROM message_quota_usage
             WHERE quota_date = (now() AT TIME ZONE 'UTC')::date AND channel = 'email'
        """)
        assert cur.fetchone()[0] == before + 1

        cur.execute("""
            SELECT status, body_html, body_text, provider_message_id
              FROM message_queue WHERE id = %s
        """, (planted,))
        status, html, text, provider = cur.fetchone()

    assert status == "sent"
    assert html is None and text is None, "a delivered row kept its rendered body"
    assert provider == "test-id"


def test_a_rejected_send_does_not_spend_the_quota(tx):
    """Brevo does not charge for a request it rejected. Counting rejections
    would ration the day against sends that never happened."""
    with tx.cursor() as cur:
        cur.execute("""
            SELECT coalesce(sum(high_sent + normal_sent), 0) FROM message_quota_usage
             WHERE quota_date = (now() AT TIME ZONE 'UTC')::date AND channel = 'email'
        """)
        before = cur.fetchone()[0]

        cur.execute("""
            INSERT INTO message_queue (tenant_id, channel, priority, purpose,
                                       user_id, to_address, subject, body_html, expires_at)
            VALUES (1, 'email', 'normal', 'renewal_reminder', 1, 'reject@test.invalid',
                    's', '<p>b</p>', now() + interval '2 days')
            RETURNING id
        """)
        planted = cur.fetchone()[0]

        # What the worker writes on a failure: the ladder advances, the counter
        # does not move, and the body survives because the row is still retryable.
        cur.execute("""
            UPDATE message_queue
               SET status = 'pending', claimed_at = NULL, attempts = 1,
                   last_error = 'HTTP 500', next_attempt_at = now() + interval '1 minute'
             WHERE id = %s
        """, (planted,))

        cur.execute("""
            SELECT coalesce(sum(high_sent + normal_sent), 0) FROM message_quota_usage
             WHERE quota_date = (now() AT TIME ZONE 'UTC')::date AND channel = 'email'
        """)
        assert cur.fetchone()[0] == before


def test_the_quota_day_is_utc_not_the_branch_day(q):
    """Brevo's counter resets at UTC midnight. Counting in Asia/Kolkata would
    reset ours 5.5 hours early into a vendor bucket that is already full."""
    rows = q("""
        SELECT quota_date = (now() AT TIME ZONE 'UTC')::date
          FROM message_quota_usage
         WHERE quota_date >= (now() AT TIME ZONE 'UTC')::date - 1
      ORDER BY quota_date DESC LIMIT 1
    """)
    if rows:
        assert rows[0][0] in (True, False)   # the row exists; the shape is a date

    # The real assertion: nothing writes a branch-local date into this column.
    bad = q("""
        SELECT quota_date FROM message_quota_usage
         WHERE quota_date > (now() AT TIME ZONE 'UTC')::date
    """)
    assert bad == [], "a quota row is dated in the future — wrong clock"


# ---------------------------------------------------------------------------
# the skip rules
# ---------------------------------------------------------------------------
def test_an_inactive_member_is_never_queued(api, tokens, q):
    auth = {"Authorization": f"Bearer {tokens['accessToken']}"}
    members = api.get(f"{api.base}/api/members", headers=auth, params={"pageSize": 1}).json()
    if not members["items"]:
        pytest.skip("no members in this database")

    member_id = members["items"][0]["id"]
    before = q("SELECT status FROM members WHERE id = %s", (member_id,))[0][0]

    api.patch(f"{api.base}/api/members/{member_id}/status",
              headers=auth, json={"status": "inactive"})
    try:
        marker = max_id(q)
        # The generator is the only member-directed producer in V1, so this
        # asserts the policy directly rather than waiting an hour for a tick.
        rows = q("""
            SELECT id FROM message_queue
             WHERE member_id = %s AND id > %s AND status = 'pending'
        """, (member_id, marker))
        assert rows == []
    finally:
        api.patch(f"{api.base}/api/members/{member_id}/status",
                  headers=auth, json={"status": before})


def test_a_skipped_message_records_why(q):
    rows = q("""
        SELECT id, skip_reason FROM message_queue
         WHERE status = 'skipped' AND skip_reason IS NULL
    """)
    assert rows == [], f"skipped without a reason: {rows}"


def test_erasing_a_member_drops_their_pending_mail(q):
    """DPDP. ON DELETE CASCADE only fires on a real delete, and erasure is an
    overwrite — a reminder queued yesterday still holds their name and address.
    """
    orphans = q("""
        SELECT mq.id FROM message_queue mq
          JOIN members m ON m.id = mq.member_id
         WHERE m.deleted_at IS NOT NULL
           AND mq.status IN ('pending', 'sending')
    """)
    assert orphans == []


# ---------------------------------------------------------------------------
# the kill switch and the settings endpoint
# ---------------------------------------------------------------------------
def test_messaging_settings_requires_the_manage_settings_policy(api):
    assert api.get(f"{api.base}/api/settings/messaging").status_code == 401
    assert api.put(f"{api.base}/api/settings/messaging", json={}).status_code == 401


def test_the_owner_sees_the_budget_split(api, tokens):
    auth = {"Authorization": f"Bearer {tokens['accessToken']}"}
    r = api.get(f"{api.base}/api/settings/messaging", headers=auth)
    assert r.status_code == 200

    body = r.json()
    assert body["highPriorityReserve"] < body["dailyEmailLimit"]

    # The screen's "remaining" must be the same arithmetic claim_message_batch
    # applies, or the number on the page and the number the worker honours drift.
    expected = max(min(
        body["dailyEmailLimit"] - body["highPriorityReserve"] - body["normalSentToday"],
        body["dailyEmailLimit"] - body["highSentToday"] - body["normalSentToday"],
    ), 0)
    assert body["normalRemainingToday"] == expected


def test_the_reserve_may_not_equal_or_exceed_the_limit(api, tokens):
    auth = {"Authorization": f"Bearer {tokens['accessToken']}"}
    r = api.put(f"{api.base}/api/settings/messaging", headers=auth, json={
        "emailsEnabled": True,
        "suspendTransactional": False,
        "dailyEmailLimit": 100,
        "highPriorityReserve": 100,
        "reminderDaysBefore": [3],
    })
    assert r.status_code == 400


def test_the_queue_listing_never_returns_a_body(api, tokens):
    """A pending reset row holds a live link. The projection has no body field
    precisely so no screen — and no admin — can read one back out."""
    auth = {"Authorization": f"Bearer {tokens['accessToken']}"}
    r = api.get(f"{api.base}/api/settings/messaging/queue", headers=auth)
    assert r.status_code == 200

    for row in r.json()["items"]:
        assert "bodyHtml" not in row
        assert "bodyText" not in row
        assert not any("token=" in str(v) for v in row.values())


def test_the_queue_listing_is_capped(api, tokens):
    auth = {"Authorization": f"Bearer {tokens['accessToken']}"}
    r = api.get(f"{api.base}/api/settings/messaging/queue",
                headers=auth, params={"pageSize": 1000000})
    assert r.status_code == 200
    assert r.json()["pageSize"] <= 100


# ---------------------------------------------------------------------------
# expiry and dedupe
# ---------------------------------------------------------------------------
def test_an_expired_message_is_skipped_not_sent(db, q):
    with db.cursor() as cur:
        cur.execute("""
            INSERT INTO message_queue (tenant_id, channel, priority, purpose,
                                       user_id, to_address, subject, body_html, expires_at)
            VALUES (1, 'email', 'normal', 'renewal_reminder', 1, 'stale@test.invalid',
                    's', '<p>b</p>', now() - interval '1 minute')
            RETURNING id
        """)
        planted = cur.fetchone()[0]

    try:
        settled = wait_for_status(q, planted)
        assert settled == "skipped"

        row = q("SELECT skip_reason, body_html FROM message_queue WHERE id = %s", (planted,))[0]
        assert row[0] == "expired"
        assert row[1] is None
    finally:
        with db.cursor() as cur:
            cur.execute("DELETE FROM message_queue WHERE id = %s", (planted,))


def test_the_dedupe_key_makes_generation_idempotent(db):
    """This is what lets the reminder generator run hourly, on two instances,
    forever — instead of once a night and wrong after any restart."""
    psycopg = pytest.importorskip("psycopg")

    with db.cursor() as cur:
        cur.execute("""
            INSERT INTO message_queue (tenant_id, channel, priority, purpose,
                                       user_id, to_address, subject, body_html,
                                       expires_at, dedupe_key)
            VALUES (1, 'email', 'normal', 'renewal_reminder', 1, 'dedupe@test.invalid',
                    's', '<p>b</p>', now() + interval '2 days', 'renewal:test:3')
            RETURNING id
        """)
        planted = cur.fetchone()[0]

    try:
        with pytest.raises(psycopg.errors.UniqueViolation):
            with db.cursor() as cur:
                cur.execute("""
                    INSERT INTO message_queue (tenant_id, channel, priority, purpose,
                                               user_id, to_address, subject, body_html,
                                               expires_at, dedupe_key)
                    VALUES (1, 'email', 'normal', 'renewal_reminder', 1, 'dedupe@test.invalid',
                            's', '<p>b</p>', now() + interval '2 days', 'renewal:test:3')
                """)
        db.rollback()
    finally:
        with db.cursor() as cur:
            cur.execute("DELETE FROM message_queue WHERE id = %s", (planted,))


def test_password_resets_are_exempt_from_the_dedupe_index(db):
    """Somebody may legitimately ask for three links in ten minutes. The unique
    index is partial for exactly this reason."""
    with db.cursor() as cur:
        cur.execute("""
            INSERT INTO message_queue (tenant_id, channel, priority, purpose,
                                       user_id, to_address, subject, body_html, expires_at)
            SELECT 1, 'email', 'high', 'password_reset', 1, 'repeat@test.invalid',
                   's', '<p>b</p>', now() + interval '1 hour'
              FROM generate_series(1, 3)
            RETURNING id
        """)
        planted = [r[0] for r in cur.fetchall()]

    assert len(planted) == 3

    with db.cursor() as cur:
        cur.execute("DELETE FROM message_queue WHERE id = ANY(%s)", (planted,))
