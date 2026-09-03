import os
import pytest
import requests

BASE_URL = os.environ.get("GYMAPI_BASE_URL", "http://localhost:5023")
OWNER_EMAIL = os.environ.get("GYMAPI_OWNER_EMAIL", "admin@steelflex.com")
OWNER_PASSWORD = os.environ.get("GYMAPI_OWNER_PASSWORD", "SteelFlex@123")
DB_DSN = os.environ.get("GYMAPI_DB_DSN", "postgresql://gmsdb@localhost/gmsdb")


@pytest.fixture(scope="session")
def api():
    """Black-box client against a RUNNING api and a REAL database — no mocks.

    Never point GYMAPI_BASE_URL at production: these tests rotate and revoke
    the owner's refresh tokens.
    """
    session = requests.Session()
    session.verify = False
    try:
        session.get(f"{BASE_URL}/openapi/v1.json", timeout=5)
    except requests.RequestException:
        pytest.skip(f"API not reachable at {BASE_URL} — start it with `dotnet run`.")
    session.base = BASE_URL
    return session


@pytest.fixture
def tokens(api):
    """A fresh login. Each test gets its own token family."""
    r = api.post(f"{api.base}/api/auth/login",
                 json={"email": OWNER_EMAIL, "password": OWNER_PASSWORD})
    assert r.status_code == 200, r.text
    return r.json()


@pytest.fixture(scope="session")
def db():
    """A direct connection to the same database the API is writing to.

    The messaging tests need it. Everything that matters about the queue is
    invisible over HTTP: forgot-password answers 202 whether it queued a message
    or not — that uniformity is a deliberate anti-enumeration control — so the
    only way to assert the message exists is to look. It also lets the tests
    check the one security property the module turns on: that a delivered row
    keeps no rendered body.

    Autocommit, and every write the tests make is cleaned up by the test that
    made it. Never point GYMAPI_DB_DSN at production.
    """
    psycopg = pytest.importorskip("psycopg", reason="pip install -r requirements.txt")

    try:
        conn = psycopg.connect(DB_DSN, autocommit=True)
    except psycopg.OperationalError as exc:
        pytest.skip(f"Database not reachable at {DB_DSN}: {exc}")

    yield conn
    conn.close()


@pytest.fixture
def q(db):
    """Query helper: q("SELECT ...", args) -> list of tuples."""
    def run(sql, params=()):
        with db.cursor() as cur:
            cur.execute(sql, params)
            return cur.fetchall() if cur.description else []
    return run


@pytest.fixture
def tx(db):
    """A rolled-back transaction for tests that exercise the SQL directly.

    The `db` connection is autocommit, which is right for reading and for the
    small fixtures that clean up after themselves. It is wrong for anything that
    calls claim_message_batch(): that function has no tenant scope — it claims
    whatever is due, including real pending mail — so a test running it outside
    a transaction leaves live messages stranded in 'sending' until the
    ten-minute rescue picks them up.

    This gives those tests their own connection, their own transaction, and an
    unconditional rollback.
    """
    psycopg = pytest.importorskip("psycopg")

    conn = psycopg.connect(DB_DSN, autocommit=False)
    try:
        with conn.cursor() as cur:
            # Park anything real that is currently due, so a budget assertion
            # counts only the rows the test planted. Rolled back with the rest.
            cur.execute("""
                UPDATE message_queue SET next_attempt_at = now() + interval '1 day'
                 WHERE status = 'pending'
            """)
            # Start every budget test from a known allowance.
            cur.execute("""
                INSERT INTO message_quota_usage (quota_date, channel, high_sent, normal_sent)
                VALUES ((now() AT TIME ZONE 'UTC')::date, 'email', 0, 0)
                ON CONFLICT (quota_date, channel) DO UPDATE
                   SET high_sent = 0, normal_sent = 0
            """)
        yield conn
    finally:
        conn.rollback()
        conn.close()
