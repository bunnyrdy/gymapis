"""Dashboard guards.

The dashboard recounts nothing: every figure on it is owned by the module that
produces it. So the tests that matter are agreement tests — if a number here
ever drifts from /api/members/stats or /api/attendance/stats, someone has
reimplemented a count instead of composing one, which is exactly the drift
v_member_overview exists to prevent.

Plus the one thing this module adds on its own: the money fields are
authorization, not data.

Black-box: needs the API running and a real database.
"""
import pytest

DASHBOARD = "/api/dashboard"


@pytest.fixture
def auth(tokens):
    return {"Authorization": f"Bearer {tokens['accessToken']}"}


@pytest.fixture
def board(api, auth):
    r = api.get(f"{api.base}{DASHBOARD}", headers=auth)
    assert r.status_code == 200, r.text
    return r.json()


# --- authorization ---------------------------------------------------------

def test_anonymous_is_rejected(api):
    """No [Authorize] attribute on the controller — the fallback policy is it."""
    r = api.get(f"{api.base}{DASHBOARD}")
    assert r.status_code == 401, r.text
    assert r.json()["code"], r.text


def test_owner_sees_the_money_fields(board):
    assert board["kpis"]["monthlyRevenue"] is not None
    assert board["kpis"]["pendingPaymentsValue"] is not None


@pytest.mark.skip(
    reason="Needs a receptionist login. The suite is black-box over HTTP and there "
           "is no user-creation endpoint, so the only seeded account is the owner. "
           "Verified manually by flipping users.role and re-logging in: 200 with "
           "monthlyRevenue and pendingPaymentsValue both null, pendingPayments "
           "still a number. Unskip once a fixture can provision a second account."
)
def test_money_is_hidden_from_a_receptionist(api):
    """
    The whole point of gating inside the service rather than on the endpoint: a
    receptionist gets the dashboard, minus two fields. The count survives — it
    is their work queue — while the totals do not.
    """


# --- agreement with the modules the numbers come from ----------------------

def test_kpis_agree_with_member_stats(api, auth, board):
    stats = api.get(f"{api.base}/api/members/stats", headers=auth).json()
    kpis = board["kpis"]

    assert kpis["activeMembers"] == stats["active"]
    assert kpis["expiringSoon"] == stats["expiringSoon"]
    assert kpis["expired"] == stats["expired"]
    assert kpis["pendingPayments"] == stats["pendingPayments"]
    assert float(kpis["pendingPaymentsValue"]) == float(stats["pendingPaymentsValue"])


def test_attendance_agrees_with_attendance_stats(api, auth, board):
    stats = api.get(f"{api.base}/api/attendance/stats", headers=auth).json()
    att = board["attendance"]

    for field in ("totalStaff", "present", "absent", "notMarked"):
        assert att[field] == stats[field], field


def test_donut_slices_sum_to_its_own_total(board):
    """
    The ring has to close. Total is the three states summed, not the member
    count — a member with no membership belongs to no slice.
    """
    mix = board["mix"]
    assert mix["active"] + mix["expiringSoon"] + mix["expired"] == mix["total"]


def test_mix_agrees_with_the_kpi_cards(board):
    mix, kpis = board["mix"], board["kpis"]
    assert mix["active"] == kpis["activeMembers"]
    assert mix["expiringSoon"] == kpis["expiringSoon"]
    assert mix["expired"] == kpis["expired"]


# --- the shortlists --------------------------------------------------------

def test_expiring_rows_are_within_the_window_and_ordered(board):
    rows = board["expiring"]
    assert len(rows) <= 5

    for row in rows:
        assert row["daysRemaining"] is not None
        assert row["daysRemaining"] <= 7, row
        # Already-expired rows belong here: they are the most urgent line on
        # the screen, so there is deliberately no lower bound.
        assert row["membershipState"] in ("active", "expiring_soon", "expired")

    dates = [row["endDate"] for row in rows]
    assert dates == sorted(dates), "soonest expiry first"


def test_not_marked_list_is_bounded_and_consistent(board):
    att = board["attendance"]
    rows = att["notMarkedStaff"]

    assert len(rows) <= 5
    assert len(rows) <= att["notMarked"]
    for row in rows:
        assert row["staffId"] and row["fullName"]
        assert row["role"] in ("trainer", "receptionist", "manager", "admin")


def test_attendance_counts_do_not_exceed_the_roster(board):
    att = board["attendance"]
    assert att["present"] + att["absent"] + att["notMarked"] <= att["totalStaff"]


# --- the feed --------------------------------------------------------------

def test_activity_is_recent_first_and_bounded(board):
    rows = board["activity"]
    assert len(rows) <= 12

    for row in rows:
        assert row["action"] and row["entityType"] and row["description"]

    stamps = [row["createdAt"] for row in rows]
    assert stamps == sorted(stamps, reverse=True), "newest first"


def test_a_mutation_shows_up_in_the_feed(api, auth, board):
    """
    activity_log has been written on every mutation since the staff module; the
    dashboard is the first thing to read it back. This proves the wiring, not
    the log.
    """
    r = api.post(f"{api.base}/api/membership-plans", headers=auth, json={
        "name": "Dashboard Feed Plan",
        "durationValue": 1,
        "durationUnit": "month",
        "price": 500.00,
        "serviceIds": [],
        "isActive": True,
    })
    assert r.status_code == 201, r.text
    plan = r.json()

    try:
        rows = api.get(f"{api.base}{DASHBOARD}", headers=auth).json()["activity"]
        newest = rows[0]
        assert newest["action"] == "plan.created", rows[:2]
        assert newest["entityId"] == plan["id"]
        assert newest["actorEmail"], "the owner's email, joined from users"
    finally:
        api.delete(f"{api.base}/api/membership-plans/{plan['id']}", headers=auth)
