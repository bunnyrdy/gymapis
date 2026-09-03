"""Membership plan guards.

Focused on the controls whose failure is silent: authorization, mass
assignment, the service-id allow-list, the page-size ceiling and archive
semantics. The Included Services catalog is read from the API rather than
hard-coded, so these tests keep working when the seed changes.
"""
import pytest

PLANS = "/api/membership-plans"


@pytest.fixture
def auth(tokens):
    return {"Authorization": f"Bearer {tokens['accessToken']}"}


@pytest.fixture
def services(api, auth):
    """The seeded plan_services catalog. Needs at least three rows."""
    r = api.get(f"{api.base}/api/lookups/plan-services", headers=auth)
    assert r.status_code == 200, r.text
    catalog = r.json()
    assert len(catalog) >= 3, "003_plan_services.sql seed has not been applied"
    return catalog


def body(**overrides):
    payload = {
        "name": "Fixture Plan",
        "durationValue": 1,
        "durationUnit": "month",
        "price": 49.00,
        "serviceIds": [],
        "isActive": True,
    }
    payload.update(overrides)
    return payload


@pytest.fixture
def plan(api, auth):
    """A plan that cleans itself up."""
    r = api.post(f"{api.base}{PLANS}", headers=auth, json=body())
    assert r.status_code == 201, r.text
    created = r.json()
    yield created
    api.delete(f"{api.base}{PLANS}/{created['id']}", headers=auth)


# --- authorization ---------------------------------------------------------

@pytest.mark.parametrize("path", [PLANS, "/api/lookups/plan-services"])
def test_anonymous_is_rejected(api, path):
    assert api.get(f"{api.base}{path}").status_code == 401


def test_anonymous_cannot_create(api):
    assert api.post(f"{api.base}{PLANS}", json=body()).status_code == 401


# --- mass assignment -------------------------------------------------------

def test_server_owned_fields_are_ignored(api, auth):
    """Id, tenancy and archived_at are not bindable. Posting them must not stick."""
    r = api.post(f"{api.base}{PLANS}", headers=auth, json=body(
        name="Mass Assign Probe",
        id=999999,
        tenantId=2,
        branchId=77,
        archivedAt="2020-01-01T00:00:00Z",
    ))
    assert r.status_code == 201, r.text
    created = r.json()
    try:
        assert created["id"] != 999999
        # An archived plan would 404 on read — proof archivedAt was not bound.
        assert api.get(f"{api.base}{PLANS}/{created['id']}", headers=auth).status_code == 200
    finally:
        api.delete(f"{api.base}{PLANS}/{created['id']}", headers=auth)


def test_plan_code_is_generated_when_blank(api, auth, plan):
    assert plan["planCode"].startswith("PLN-")


# --- validation ------------------------------------------------------------

@pytest.mark.parametrize("payload", [
    {"name": ""},                       # required
    {"name": "x"},                      # under min length
    {"durationUnit": "year"},           # not in the CHECK constraint
    {"durationValue": 0},               # must be > 0
    {"price": -1},                      # must be >= 0
    {"planCode": "not a code!"},        # charset
])
def test_bad_input_is_400(api, auth, payload):
    assert api.post(f"{api.base}{PLANS}", headers=auth, json=body(**payload)).status_code == 400


def test_duplicate_service_ids_are_400(api, auth, services):
    sid = services[0]["id"]
    r = api.post(f"{api.base}{PLANS}", headers=auth, json=body(serviceIds=[sid, sid]))
    assert r.status_code == 400, r.text


def test_unknown_service_id_is_400_not_500(api, auth):
    """The allow-list guard. Without it a crafted body attaches arbitrary rows."""
    r = api.post(f"{api.base}{PLANS}", headers=auth, json=body(serviceIds=[10**9]))
    assert r.status_code == 400, r.text


def test_page_size_is_capped(api, auth):
    """Without a ceiling, ?pageSize=1000000 is a bulk export and a DoS in one request."""
    assert api.get(f"{api.base}{PLANS}?pageSize=1000000", headers=auth).status_code == 400


def test_duplicate_plan_code_is_400_not_500(api, auth, plan):
    r = api.post(f"{api.base}{PLANS}", headers=auth, json=body(planCode=plan["planCode"]))
    assert r.status_code == 400, r.text


# --- IDOR ------------------------------------------------------------------

@pytest.mark.parametrize("method", ["get", "put", "delete"])
def test_unknown_id_is_404(api, auth, method):
    url = f"{api.base}{PLANS}/999999999"
    call = getattr(api, method)
    r = call(url, headers=auth, json=body()) if method == "put" else call(url, headers=auth)
    assert r.status_code == 404


# --- services round-trip ---------------------------------------------------

def test_services_round_trip(api, auth, services):
    chosen = [s["id"] for s in services[:3]]
    r = api.post(f"{api.base}{PLANS}", headers=auth, json=body(serviceIds=chosen))
    assert r.status_code == 201, r.text
    created = r.json()

    try:
        assert sorted(s["id"] for s in created["services"]) == sorted(chosen)

        # Editing replaces the set rather than appending to it.
        kept = chosen[:1]
        r = api.put(f"{api.base}{PLANS}/{created['id']}", headers=auth,
                    json=body(serviceIds=kept))
        assert r.status_code == 200, r.text
        assert [s["id"] for s in r.json()["services"]] == kept

        fetched = api.get(f"{api.base}{PLANS}/{created['id']}", headers=auth).json()
        assert [s["id"] for s in fetched["services"]] == kept
    finally:
        api.delete(f"{api.base}{PLANS}/{created['id']}", headers=auth)


# --- behaviour -------------------------------------------------------------

def test_visibility_toggle_round_trips(api, auth, plan):
    r = api.put(f"{api.base}{PLANS}/{plan['id']}", headers=auth, json=body(isActive=False))
    assert r.status_code == 200, r.text
    assert r.json()["isActive"] is False

    listed = api.get(f"{api.base}{PLANS}?status=active&pageSize=100", headers=auth).json()
    assert plan["id"] not in [p["id"] for p in listed["items"]]


def test_archive_is_soft_and_hides_the_plan(api, auth):
    created = api.post(f"{api.base}{PLANS}", headers=auth,
                       json=body(name="Archive Me")).json()

    assert api.delete(f"{api.base}{PLANS}/{created['id']}", headers=auth).status_code == 204
    assert api.get(f"{api.base}{PLANS}/{created['id']}", headers=auth).status_code == 404

    listed = api.get(f"{api.base}{PLANS}?pageSize=100", headers=auth).json()
    assert created["id"] not in [p["id"] for p in listed["items"]]

    # Archiving twice is a 404, not a second archive.
    assert api.delete(f"{api.base}{PLANS}/{created['id']}", headers=auth).status_code == 404


def test_list_reports_service_count(api, auth, services):
    chosen = [s["id"] for s in services[:2]]
    created = api.post(f"{api.base}{PLANS}", headers=auth,
                       json=body(name="Counted Plan", serviceIds=chosen)).json()
    try:
        listed = api.get(f"{api.base}{PLANS}?search=Counted Plan", headers=auth).json()
        row = next(p for p in listed["items"] if p["id"] == created["id"])
        assert row["serviceCount"] == len(chosen)
    finally:
        api.delete(f"{api.base}{PLANS}/{created['id']}", headers=auth)
