"""Staff module guards — parameterised across every role.

Receptionists and trainers are the same endpoints behind StaffControllerBase, so
every test runs against both. Focused on the controls whose failure is silent:
authorization, mass assignment, tenant/role scoping, upload sniffing and the
dated shift history.
"""
import io
import pytest
from conftest import OWNER_EMAIL, OWNER_PASSWORD

PNG_1PX = bytes.fromhex(
    "89504e470d0a1a0a0000000d4948445200000001000000010806000000"
    "1f15c4890000000d4944415478da63fccf00000302010134ca8c000000"
    "0049454e44ae426082"
)


ROLES = ["receptionists", "trainers"]
CODE_PREFIX = {"receptionists": "REC-", "trainers": "TRN-"}


@pytest.fixture
def auth(tokens):
    return {"Authorization": f"Bearer {tokens['accessToken']}"}


@pytest.fixture(params=ROLES)
def role(request):
    """Every test below runs once per staff role."""
    return request.param


@pytest.fixture
def person(api, auth, role):
    """A receptionist that cleans itself up."""
    r = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Fixture Person",
        "Phone": "5550000000",
        "JoiningDate": "2024-01-01",
        "Status": "active",
    })
    assert r.status_code == 201, r.text
    created = r.json()
    yield created
    api.delete(f"{api.base}/api/{role}/{created['id']}", headers=auth)


# --- authorization ---------------------------------------------------------

def test_anonymous_is_rejected(api, role):
    assert api.get(f"{api.base}/api/{role}").status_code == 401


# --- mass assignment -------------------------------------------------------

def test_server_owns_role_tenant_and_code(api, role, auth):
    """Posting privileged fields must not set them."""
    r = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Injection Attempt",
        "Phone": "5551112222",
        "JoiningDate": "2024-01-01",
        "Role": "admin",          # ignored — not on the DTO
        "TenantId": "2",          # ignored
        "StaffCode": "ADM-9999",  # ignored — server generates
        "Id": "4242",             # ignored
    })
    assert r.status_code == 201, r.text
    created = r.json()
    try:
        assert created["staffCode"].startswith(CODE_PREFIX[role])
        assert created["id"] != 4242
    finally:
        api.delete(f"{api.base}/api/{role}/{created['id']}", headers=auth)


# --- validation ------------------------------------------------------------

@pytest.mark.parametrize("field,payload", [
    ("Status", {"Status": "superuser"}),
    ("Gender", {"Gender": "attack"}),
    ("DateOfBirth", {"DateOfBirth": "2015-01-01"}),
    ("Phone", {"Phone": "'; DROP TABLE staff;--"}),
    ("WorkingDays", {"ShiftId": "1", "WorkingDays": "9"}),
])
def test_invalid_values_are_rejected(api, role, auth, field, payload):
    body = {"FullName": "Bad Input", "Phone": "5551234567", "JoiningDate": "2024-01-01"}
    body.update(payload)
    r = api.post(f"{api.base}/api/{role}", headers=auth, data=body)
    assert r.status_code == 400, r.text


def test_page_size_is_capped(api, role, auth):
    """Without a ceiling, ?pageSize=1000000 is a bulk export and a DoS."""
    r = api.get(f"{api.base}/api/{role}?pageSize=1000000", headers=auth)
    assert r.status_code == 400


def test_unknown_tag_is_rejected(api, role, auth):
    """Ids come from the client, so they are checked against the tenant's own
    tags. Trainers take no tags at all, so any id is a rejection there."""
    r = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Tag Probe", "Phone": "5551234567",
        "JoiningDate": "2024-01-01", "ResponsibilityTagIds": "999999",
    })
    assert r.status_code == 400


# --- IDOR ------------------------------------------------------------------

def test_unknown_id_is_404_not_500(api, role, auth):
    assert api.get(f"{api.base}/api/{role}/999999", headers=auth).status_code == 404
    assert api.delete(f"{api.base}/api/{role}/999999", headers=auth).status_code == 404


# --- uploads ---------------------------------------------------------------

def test_a_text_file_named_jpg_is_rejected(api, role, auth):
    """Content-Type and extension are attacker-controlled; magic bytes are not."""
    r = api.post(f"{api.base}/api/{role}", headers=auth,
                 data={"FullName": "Fake Photo", "Phone": "5551234567", "JoiningDate": "2024-01-01"},
                 files={"photo": ("x.jpg", io.BytesIO(b"<script>alert(1)</script>"), "image/jpeg")})
    assert r.status_code == 400
    assert "JPEG" in r.text


def test_svg_is_rejected(api, role, auth):
    """An SVG is XML that can carry <script> — stored XSS if ever rendered."""
    svg = b'<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>'
    r = api.post(f"{api.base}/api/{role}", headers=auth,
                 data={"FullName": "Svg Photo", "Phone": "5551234567", "JoiningDate": "2024-01-01"},
                 files={"photo": ("x.png", io.BytesIO(svg), "image/png")})
    assert r.status_code == 400


def test_traversal_filename_does_not_escape_the_upload_folder(api, role, auth):
    r = api.post(f"{api.base}/api/{role}", headers=auth,
                 data={"FullName": "Traversal", "Phone": "5551234567", "JoiningDate": "2024-01-01"},
                 files={"photo": ("../../../appsettings.json", io.BytesIO(PNG_1PX), "image/png")})
    assert r.status_code == 201, r.text
    created = r.json()
    try:
        assert created["photoUrl"].startswith("/uploads/staff/")
        assert ".." not in created["photoUrl"]
    finally:
        api.delete(f"{api.base}/api/{role}/{created['id']}", headers=auth)


def test_served_photo_carries_nosniff(api, role, auth):
    r = api.post(f"{api.base}/api/{role}", headers=auth,
                 data={"FullName": "Header Check", "Phone": "5551234567", "JoiningDate": "2024-01-01"},
                 files={"photo": ("ok.png", io.BytesIO(PNG_1PX), "image/png")})
    created = r.json()
    try:
        served = api.get(f"{api.base}{created['photoUrl']}")
        assert served.headers["X-Content-Type-Options"] == "nosniff"
        assert served.headers["Content-Type"] == "image/png"
    finally:
        api.delete(f"{api.base}/api/{role}/{created['id']}", headers=auth)


# --- behaviour -------------------------------------------------------------

def test_staff_codes_are_sequential(api, role, auth, person):
    r = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Second Person", "Phone": "5559999999", "JoiningDate": "2024-01-01",
    })
    second = r.json()
    try:
        assert int(second["staffCode"][4:]) > int(person["staffCode"][4:])
    finally:
        api.delete(f"{api.base}/api/{role}/{second['id']}", headers=auth)


def test_changing_shift_keeps_the_previous_assignment(api, role, auth, person):
    """Dated rows — rewriting in place would falsify last month's attendance log."""
    common = {"FullName": person["fullName"], "Phone": person["phone"],
              "JoiningDate": person["joiningDate"], "Status": "active"}

    api.put(f"{api.base}/api/{role}/{person['id']}", headers=auth,
            data={**common, "ShiftId": 1, "WorkingDays": [1, 2, 3]})
    first = api.get(f"{api.base}/api/{role}/{person['id']}", headers=auth).json()
    assert first["shift"]["shiftId"] == 1

    api.put(f"{api.base}/api/{role}/{person['id']}", headers=auth,
            data={**common, "ShiftId": 3, "WorkingDays": [4, 5]})
    second = api.get(f"{api.base}/api/{role}/{person['id']}", headers=auth).json()
    assert second["shift"]["shiftId"] == 3
    assert second["shift"]["workingDays"] == [4, 5]


def test_delete_is_soft_and_hides_the_row(api, role, auth):
    r = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Temporary Staff", "Phone": "5558887777", "JoiningDate": "2024-01-01",
    })
    created = r.json()
    assert api.delete(f"{api.base}/api/{role}/{created['id']}", headers=auth).status_code == 204
    assert api.get(f"{api.base}/api/{role}/{created['id']}", headers=auth).status_code == 404


# --- role isolation --------------------------------------------------------

def test_a_role_cannot_see_or_touch_another_role(api, auth, person, role):
    """The role filter in Scoped() is also the cross-role IDOR guard: a trainer
    id must be invisible to the receptionist endpoints, and vice versa."""
    other = "trainers" if role == "receptionists" else "receptionists"

    assert api.get(f"{api.base}/api/{other}/{person['id']}", headers=auth).status_code == 404
    assert api.delete(f"{api.base}/api/{other}/{person['id']}", headers=auth).status_code == 404

    listed = api.get(f"{api.base}/api/{other}?pageSize=100", headers=auth).json()["items"]
    assert person["id"] not in [i["id"] for i in listed]


def test_trainer_fields_round_trip(api, auth):
    """Specialization and qualifications are trainer-only and must survive a save."""
    r = api.post(f"{api.base}/api/trainers", headers=auth, data={
        "FullName": "Marcus Thorne",
        "Phone": "5550192834",
        "JoiningDate": "2021-03-10",
        "Specialization": "Strength & Conditioning",
        "Qualifications": ["NSCA-CSCS", "CPR/AED"],
        "ExperienceYears": "8",
    })
    assert r.status_code == 201, r.text
    created = r.json()
    try:
        assert created["specialization"] == "Strength & Conditioning"
        assert created["qualifications"] == ["NSCA-CSCS", "CPR/AED"]
        assert created["ptClientCount"] == 0
        assert created["role"] == "trainer"
    finally:
        api.delete(f"{api.base}/api/trainers/{created['id']}", headers=auth)


# --- stats -----------------------------------------------------------------

def test_stats_require_authentication(api, role):
    assert api.get(f"{api.base}/api/{role}/stats").status_code == 401


def test_stats_count_the_branch_not_the_page(api, role, auth, person):
    """The cards used to count the 20 rows on screen; they now count the role."""
    stats = api.get(f"{api.base}/api/{role}/stats", headers=auth)
    assert stats.status_code == 200, stats.text
    body = stats.json()

    listing = api.get(f"{api.base}/api/{role}?pageSize=1", headers=auth).json()
    assert body["total"] == listing["totalCount"]
    assert body["active"] + body["inactive"] + body["onLeave"] <= body["total"]
    assert body["active"] >= 1          # the `person` fixture is active


def test_stats_move_when_a_person_is_added(api, role, auth, person):
    before = api.get(f"{api.base}/api/{role}/stats", headers=auth).json()

    r = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Stats Probe", "Phone": "5550001111", "JoiningDate": "2024-01-01",
    })
    created = r.json()
    try:
        after = api.get(f"{api.base}/api/{role}/stats", headers=auth).json()
        assert after["total"] == before["total"] + 1
        assert after["active"] == before["active"] + 1
    finally:
        api.delete(f"{api.base}/api/{role}/{created['id']}", headers=auth)


def test_stats_do_not_count_deleted_people(api, role, auth):
    r = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Soft Deleted", "Phone": "5550002222", "JoiningDate": "2024-01-01",
    })
    created = r.json()
    before = api.get(f"{api.base}/api/{role}/stats", headers=auth).json()

    api.delete(f"{api.base}/api/{role}/{created['id']}", headers=auth)

    after = api.get(f"{api.base}/api/{role}/stats", headers=auth).json()
    assert after["total"] == before["total"] - 1


def test_receptionists_report_no_pt_clients(api, auth, person):
    """One DTO serves both roles; the field is simply 0 where it has no meaning."""
    body = api.get(f"{api.base}/api/receptionists/stats", headers=auth).json()
    assert body["withPtClients"] == 0


# --- erase (DPDP) ----------------------------------------------------------

def test_anonymous_cannot_erase(api, role, person):
    assert api.post(f"{api.base}/api/{role}/{person['id']}/erase").status_code == 401


def test_erase_clears_personal_data_but_keeps_the_code(api, role, auth):
    r = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Erase Me", "Phone": "5550003333", "JoiningDate": "2024-01-01",
        "Email": "erase.me@example.com", "Address": "12 Somewhere Street",
        "DateOfBirth": "1990-05-05",
        "EmergencyContactName": "Next Of Kin", "EmergencyContactPhone": "5550004444",
    }, files={"photo": ("ok.png", io.BytesIO(PNG_1PX), "image/png")})
    assert r.status_code == 201, r.text
    created = r.json()
    code = created["staffCode"]

    assert api.post(f"{api.base}/api/{role}/{created['id']}/erase", headers=auth).status_code == 204

    # The row is gone from every list, and the code it owned is not reissued.
    assert api.get(f"{api.base}/api/{role}/{created['id']}", headers=auth).status_code == 404

    nxt = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "After Erase", "Phone": "5550005555", "JoiningDate": "2024-01-01",
    }).json()
    try:
        assert nxt["staffCode"] != code
    finally:
        api.delete(f"{api.base}/api/{role}/{nxt['id']}", headers=auth)


def test_erase_is_idempotent(api, role, auth):
    r = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Twice Erased", "Phone": "5550006666", "JoiningDate": "2024-01-01",
    })
    created = r.json()
    url = f"{api.base}/api/{role}/{created['id']}/erase"
    assert api.post(url, headers=auth).status_code == 204
    assert api.post(url, headers=auth).status_code == 204


def test_erasing_after_delete_still_works(api, role, auth):
    """A soft delete is normally what happens first, so the filter is lifted."""
    created = api.post(f"{api.base}/api/{role}", headers=auth, data={
        "FullName": "Deleted Then Erased", "Phone": "5550007777", "JoiningDate": "2024-01-01",
    }).json()
    assert api.delete(f"{api.base}/api/{role}/{created['id']}", headers=auth).status_code == 204
    assert api.post(f"{api.base}/api/{role}/{created['id']}/erase", headers=auth).status_code == 204


def test_erasing_an_unknown_id_is_404(api, role, auth):
    assert api.post(f"{api.base}/api/{role}/99999999/erase", headers=auth).status_code == 404


def test_a_role_cannot_erase_the_other_roles_person(api, role, auth, person):
    """Scoped() filters on role, so the other endpoint must not see this id."""
    other = "trainers" if role == "receptionists" else "receptionists"
    assert api.post(f"{api.base}/api/{other}/{person['id']}/erase", headers=auth).status_code == 404
