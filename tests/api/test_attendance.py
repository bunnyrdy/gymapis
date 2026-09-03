"""Attendance module guards.

The module has three things worth guarding and they are all invisible when they
break: the derived statuses (only two of the four are stored), the duplicate
key, and the split between marking — which a receptionist may do — and
correcting, which they may not.

Every test builds its own staff member with its own shift roster, because the
statuses under test are a function of `working_days` and the shift end time.
"""
import datetime as dt

import pytest

ATT = "/api/attendance"

# The seeded shifts. Morning is the one that has certainly ended by the time a
# test runs; Flex has NULL start/end and so has no end-of-day at all.
MORNING = "Morning"
FLEX = "Flex"

ALL_DAYS = [1, 2, 3, 4, 5, 6, 7]


@pytest.fixture
def auth(tokens):
    return {"Authorization": f"Bearer {tokens['accessToken']}"}


@pytest.fixture
def shifts(api, auth):
    r = api.get(f"{api.base}/api/lookups/shifts", headers=auth)
    assert r.status_code == 200, r.text
    return {s["name"]: s for s in r.json()}


@pytest.fixture
def make_staff(api, auth, shifts):
    """Creates trainers with a chosen shift and working days; cleans them up."""
    created = []

    def _make(shift_name=MORNING, working_days=None, joining="2024-01-01", status="active"):
        shift = shifts[shift_name]
        payload = {
            "FullName": "Attendance Fixture",
            "Phone": "5550001111",
            "JoiningDate": joining,
            "Status": status,
            "ShiftId": shift["id"],
        }
        for i, d in enumerate(ALL_DAYS if working_days is None else working_days):
            payload[f"WorkingDays[{i}]"] = d

        r = api.post(f"{api.base}/api/trainers", headers=auth, data=payload)
        assert r.status_code == 201, r.text
        person = r.json()
        created.append(person["id"])
        return person

    yield _make

    for staff_id in created:
        api.delete(f"{api.base}/api/trainers/{staff_id}", headers=auth)


@pytest.fixture
def person(make_staff):
    return make_staff()


def branch_today(api, auth, staff_id):
    """Today according to the branch's timezone, not this machine's."""
    r = api.get(f"{api.base}{ATT}/staff/{staff_id}", headers=auth)
    assert r.status_code == 200, r.text
    return r.json()["today"]


# --- authorization ---------------------------------------------------------

@pytest.mark.parametrize("method,path", [
    ("get", ATT),
    ("get", f"{ATT}/stats"),
    ("get", f"{ATT}/staff/1"),
    ("get", f"{ATT}/staff/1/log"),
    ("post", f"{ATT}/mark"),
    ("put", f"{ATT}/1"),
])
def test_requires_authentication(api, method, path):
    """The fallback policy covers reads; the two writes add their own on top."""
    r = getattr(api, method)(f"{api.base}{path}")
    assert r.status_code == 401


# --- mass assignment -------------------------------------------------------

def test_mark_ignores_server_owned_fields(api, auth, person):
    """
    Posting the fields the server derives must not set them. `markedVia` is the
    tell: a client-set 'system' would let someone forge an auto-closed row and
    disown their own edit.
    """
    today = branch_today(api, auth, person["id"])

    r = api.post(f"{api.base}{ATT}/mark", headers=auth, json={
        "staffId": person["id"],
        "date": today,
        "status": "present",
        "markedVia": "system",
        "isLate": True,
        "markedByUserId": 99999,
        "tenantId": 2,
        "branchId": 2,
        "checkOutAt": "2030-01-01T00:00:00Z",
    })
    assert r.status_code == 200, r.text
    body = r.json()

    assert body["markedVia"] == "manual"
    assert body["checkOutAt"] is None
    # is_late is derived from the shift start, never accepted from the caller.
    assert isinstance(body["isLate"], bool)


# --- duplicate prevention --------------------------------------------------

def test_duplicate_mark_is_rejected(api, auth, person):
    """UNIQUE (staff_id, attendance_date) has an envelope in front of it."""
    today = branch_today(api, auth, person["id"])
    payload = {"staffId": person["id"], "date": today, "status": "present"}

    first = api.post(f"{api.base}{ATT}/mark", headers=auth, json=payload)
    assert first.status_code == 200, first.text

    second = api.post(f"{api.base}{ATT}/mark", headers=auth, json=payload)
    assert second.status_code == 409

    # And it did not write a second row.
    log = api.get(f"{api.base}{ATT}/staff/{person['id']}/log",
                  headers=auth, params={"from": today, "to": today})
    assert log.json()["totalCount"] == 1


# --- validation ------------------------------------------------------------

def test_future_date_is_rejected(api, auth, person):
    future = (dt.date.today() + dt.timedelta(days=30)).isoformat()
    r = api.post(f"{api.base}{ATT}/mark", headers=auth, json={
        "staffId": person["id"], "date": future, "status": "present",
    })
    assert r.status_code == 400


@pytest.mark.parametrize("status", ["late", "leave", "half_day", "holiday", "week_off", "", "PRESENT"])
def test_only_present_and_absent_are_writable(api, auth, person, status):
    """
    The DB CHECK still permits seven values. V1 writes two, and the DTO is what
    holds that line — otherwise the roster would grow states no screen renders.
    """
    today = branch_today(api, auth, person["id"])
    r = api.post(f"{api.base}{ATT}/mark", headers=auth, json={
        "staffId": person["id"], "date": today, "status": status,
    })
    assert r.status_code == 400


def test_page_size_is_capped(api, auth):
    r = api.get(f"{api.base}{ATT}", headers=auth, params={"pageSize": 1_000_000})
    assert r.status_code == 400


def test_unknown_filter_value_is_ignored_not_rejected(api, auth):
    """A stale bookmark should show the roster, not a 400."""
    r = api.get(f"{api.base}{ATT}", headers=auth, params={"status": "nonsense", "role": "wizard"})
    assert r.status_code == 200


# --- scoping / IDOR --------------------------------------------------------

def test_unknown_staff_detail_is_not_found(api, auth):
    assert api.get(f"{api.base}{ATT}/staff/99999999", headers=auth).status_code == 404
    assert api.get(f"{api.base}{ATT}/staff/99999999/log", headers=auth).status_code == 404


def test_unknown_record_correction_is_not_found(api, auth):
    r = api.put(f"{api.base}{ATT}/99999999", headers=auth, json={"status": "absent"})
    assert r.status_code == 404


def test_deleted_staff_disappears_from_the_roster(api, auth, make_staff):
    """Soft delete must remove someone from the register, not just the list."""
    person = make_staff()
    today = branch_today(api, auth, person["id"])

    before = api.get(f"{api.base}{ATT}", headers=auth, params={"date": today, "pageSize": 100})
    assert any(r["staffId"] == person["id"] for r in before.json()["items"])

    api.delete(f"{api.base}/api/trainers/{person['id']}", headers=auth)

    after = api.get(f"{api.base}{ATT}", headers=auth, params={"date": today, "pageSize": 100})
    assert not any(r["staffId"] == person["id"] for r in after.json()["items"])
    assert api.get(f"{api.base}{ATT}/staff/{person['id']}", headers=auth).status_code == 404


# --- derived statuses ------------------------------------------------------

def test_non_working_day_is_week_off_not_absent(api, auth, make_staff):
    """
    working_days is ISO 1=Mon..7=Sun. A day off the roster must never be counted
    against someone — that is the difference between EXTRACT(isodow) and
    EXTRACT(dow), and getting it wrong shifts everyone's week by a day.
    """
    person = make_staff(working_days=[1])          # Mondays only
    detail = api.get(f"{api.base}{ATT}/staff/{person['id']}", headers=auth).json()

    by_date = {d["date"]: d["status"] for d in detail["days"]}
    joined = dt.date.fromisoformat(detail["joiningDate"])

    for iso_date, status in by_date.items():
        day = dt.date.fromisoformat(iso_date)
        if day < joined:
            continue
        if day.isoweekday() != 1:
            assert status == "week_off", f"{iso_date} ({day.strftime('%A')}) -> {status}"


def test_days_before_hiring_are_not_counted(api, auth, make_staff):
    """Nothing was expected of someone before they were hired."""
    today = dt.date.today()
    joining = today.replace(day=15) if today.day > 15 else today

    person = make_staff(joining=joining.isoformat())
    detail = api.get(f"{api.base}{ATT}/staff/{person['id']}", headers=auth).json()

    early = [d for d in detail["days"] if dt.date.fromisoformat(d["date"]) < joining]
    assert early, "expected some days before the joining date in this month"
    assert all(d["status"] == "week_off" for d in early)


def test_flex_shift_is_never_auto_absent(api, auth, make_staff):
    """
    A Flex shift has NULL start and end times, so it has no end-of-day to close.
    Auto-marking it absent would invent a fact nobody can check.
    """
    person = make_staff(shift_name=FLEX)
    today = branch_today(api, auth, person["id"])

    # A read triggers the closeout sweep before answering.
    api.get(f"{api.base}{ATT}", headers=auth, params={"date": today})

    detail = api.get(f"{api.base}{ATT}/staff/{person['id']}", headers=auth).json()
    by_date = {d["date"]: d for d in detail["days"]}

    assert by_date[today]["status"] == "not_marked"
    assert all(d["status"] != "absent" for d in detail["days"])


def test_stats_agree_with_the_rows(api, auth, person):
    """
    The cards are counted from the same builder as the table. If they ever
    diverge, the page contradicts itself and both numbers become untrustworthy.
    """
    today = branch_today(api, auth, person["id"])

    rows = api.get(f"{api.base}{ATT}", headers=auth,
                   params={"date": today, "pageSize": 100}).json()["items"]
    stats = api.get(f"{api.base}{ATT}/stats", headers=auth, params={"date": today}).json()

    assert stats["totalStaff"] == len(rows)
    assert stats["present"] == sum(1 for r in rows if r["status"] == "present")
    assert stats["absent"] == sum(1 for r in rows if r["status"] == "absent")
    assert stats["notMarked"] == sum(1 for r in rows if r["status"] == "not_marked")


# --- correction ------------------------------------------------------------

def test_correction_rewrites_the_record_and_clears_the_check_in(api, auth, person):
    """An absence carrying an arrival time is a contradiction the log would keep."""
    today = branch_today(api, auth, person["id"])

    marked = api.post(f"{api.base}{ATT}/mark", headers=auth, json={
        "staffId": person["id"], "date": today, "status": "present",
    }).json()
    assert marked["checkInAt"] is not None

    corrected = api.put(f"{api.base}{ATT}/{marked['id']}", headers=auth,
                        json={"status": "absent", "notes": "left early, never signed in"})
    assert corrected.status_code == 200, corrected.text
    body = corrected.json()

    assert body["status"] == "absent"
    assert body["checkInAt"] is None
    assert body["isLate"] is False
    assert body["markedVia"] == "manual"


def test_correction_does_not_create_a_second_row(api, auth, person):
    today = branch_today(api, auth, person["id"])
    marked = api.post(f"{api.base}{ATT}/mark", headers=auth, json={
        "staffId": person["id"], "date": today, "status": "present",
    }).json()

    api.put(f"{api.base}{ATT}/{marked['id']}", headers=auth, json={"status": "absent"})

    log = api.get(f"{api.base}{ATT}/staff/{person['id']}/log",
                  headers=auth, params={"from": today, "to": today}).json()
    assert log["totalCount"] == 1
    assert log["items"][0]["id"] == marked["id"]


@pytest.mark.parametrize("status", ["leave", "holiday", "nonsense"])
def test_correction_rejects_unstorable_statuses(api, auth, person, status):
    today = branch_today(api, auth, person["id"])
    marked = api.post(f"{api.base}{ATT}/mark", headers=auth, json={
        "staffId": person["id"], "date": today, "status": "present",
    }).json()

    r = api.put(f"{api.base}{ATT}/{marked['id']}", headers=auth, json={"status": status})
    assert r.status_code == 400


# --- closeout --------------------------------------------------------------

def test_closeout_is_idempotent(api, auth, person):
    """
    Two reads in a row must not produce two rows. The sweep runs behind a rate
    limit, so this mostly proves the ON CONFLICT: whatever the sweep did the
    first time, doing it again changes nothing.
    """
    today = branch_today(api, auth, person["id"])

    for _ in range(3):
        api.get(f"{api.base}{ATT}", headers=auth, params={"date": today})

    log = api.get(f"{api.base}{ATT}/staff/{person['id']}/log", headers=auth,
                  params={"pageSize": 100}).json()

    dates = [item["date"] for item in log["items"]]
    assert len(dates) == len(set(dates)), "the closeout wrote a duplicate day"


def test_auto_closed_rows_are_attributed_to_the_system(api, auth, make_staff):
    """
    An auto-closed row has no human actor. Recording it as 'manual' would be a
    lie the audit trail could never untangle.
    """
    person = make_staff(shift_name=MORNING)
    today = branch_today(api, auth, person["id"])
    api.get(f"{api.base}{ATT}", headers=auth, params={"date": today})

    log = api.get(f"{api.base}{ATT}/staff/{person['id']}/log", headers=auth,
                  params={"pageSize": 100}).json()

    auto = [i for i in log["items"] if i["markedVia"] == "system"]
    for item in auto:
        assert item["status"] == "absent"
        assert item["checkInAt"] is None


# --- shape -----------------------------------------------------------------

def test_detail_reports_zeros_rather_than_404_for_an_empty_month(api, auth, person):
    """
    v_staff_attendance_monthly is an INNER JOIN, so a month with no rows returns
    nothing at all. That must read as zeros, not as a missing staff member.
    """
    r = api.get(f"{api.base}{ATT}/staff/{person['id']}", headers=auth,
                params={"month": "2020-01-01"})
    assert r.status_code == 200, r.text
    body = r.json()

    assert body["summary"]["workingDays"] == 0
    assert body["summary"]["daysPresent"] == 0
    assert body["summary"]["attendancePct"] is None
    assert len(body["days"]) == 31          # January
    assert body["month"] == "2020-01-01"


def test_roster_reports_the_shift_in_force_on_that_date(api, auth, person):
    today = branch_today(api, auth, person["id"])
    rows = api.get(f"{api.base}{ATT}", headers=auth,
                   params={"date": today, "search": person["staffCode"]}).json()["items"]

    assert len(rows) == 1
    row = rows[0]
    assert row["staffCode"] == person["staffCode"]
    assert row["shiftName"] == MORNING
    assert row["shiftStart"] is not None and row["shiftEnd"] is not None


# --- role split ------------------------------------------------------------

@pytest.mark.skip(
    reason="Needs a receptionist login. The suite is black-box over HTTP and there "
           "is no user-creation endpoint, so the only seeded account is the owner. "
           "Verified manually by flipping users.role and re-logging in: read 200, "
           "mark today 200, back-dated mark 403, correction 403, create staff 403. "
           "Unskip once a fixture can provision a second account."
)
def test_receptionist_may_mark_but_not_correct(api):
    """
    The whole point of splitting MarkAttendance from ManageStaff. A receptionist
    takes the register; only a manager rewrites it, and neither can create staff.
    """
