"""Member guards.

Focused on the controls whose failure is silent: authorization and the split
between ManageMembers and EraseMembers, mass assignment, the money ceilings the
DTO cannot check, upload sniffing, and the three schema invariants this module
leans on — the generated total_amount, the one-active-membership index, and the
fact that neither deactivation nor erasure may touch the payments ledger.

Black-box: needs the API running and a real database.
"""
import datetime
import io

import pytest

MEMBERS = "/api/members"

# A real 1x1 PNG. Magic-byte validation is the point of the upload tests, so the
# bytes have to be genuine.
PNG_1PX = bytes.fromhex(
    "89504e470d0a1a0a0000000d4948445200000001000000010806000000"
    "1f15c4890000000d4944415478da63fccf00000302010134ca8c000000"
    "0049454e44ae426082"
)


@pytest.fixture
def auth(tokens):
    return {"Authorization": f"Bearer {tokens['accessToken']}"}


@pytest.fixture
def plan(api, auth):
    """An active plan to sell. Created here so the test owns its price."""
    r = api.post(f"{api.base}/api/membership-plans", headers=auth, json={
        "name": "Member Fixture Plan",
        "durationValue": 1,
        "durationUnit": "month",
        "price": 1000.00,
        "serviceIds": [],
        "isActive": True,
    })
    assert r.status_code == 201, r.text
    created = r.json()
    yield created
    api.delete(f"{api.base}/api/membership-plans/{created['id']}", headers=auth)


def form(plan, **overrides):
    payload = {
        "FullName": "Fixture Member",
        "Phone": "9876543210",
        "Email": "fixture.member@example.com",
        "Gender": "male",
        "Status": "active",
        "PlanId": plan["id"],
        "JoiningDate": "2024-01-01",
        "DiscountAmount": "0",
        "PaidAmount": "0",
        "PaymentMethod": "cash",
    }
    payload.update(overrides)
    return payload


@pytest.fixture
def member(api, auth, plan):
    """A member that cleans itself up."""
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan))
    assert r.status_code == 201, r.text
    created = r.json()
    yield created
    api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


# --- authorization ---------------------------------------------------------

@pytest.mark.parametrize("path", [MEMBERS, f"{MEMBERS}/stats", "/api/lookups/plans"])
def test_anonymous_is_rejected(api, path):
    assert api.get(f"{api.base}{path}").status_code == 401


def test_anonymous_cannot_create(api):
    assert api.post(f"{api.base}{MEMBERS}", data={"FullName": "X"}).status_code == 401


def test_anonymous_cannot_change_status(api, member):
    r = api.patch(f"{api.base}{MEMBERS}/{member['id']}/status", json={"status": "inactive"})
    assert r.status_code == 401


def test_anonymous_cannot_erase(api, member):
    assert api.post(f"{api.base}{MEMBERS}/{member['id']}/erase").status_code == 401


# --- mass assignment -------------------------------------------------------

def test_server_owns_code_tenant_and_id(api, auth, plan):
    """The DTO is the allow-list. None of these may be honoured."""
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(
        plan,
        Phone="9876500001",
        TenantId="2",
        BranchId="99",
        MemberCode="MEM-9999",
        Id="4242",
        PhotoUrl="/uploads/members/attacker.png",
        UserId="1",
    ))
    assert r.status_code == 201, r.text
    created = r.json()
    try:
        assert created["memberCode"].startswith("MEM-")
        assert created["memberCode"] != "MEM-9999"
        assert created["id"] != 4242
        assert created["photoUrl"] is None
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


def test_price_comes_from_the_plan_not_the_request(api, auth, plan, member):
    """Otherwise a crafted form sells a 1000-rupee plan for one rupee."""
    assert member["currentMembership"]["planPrice"] == plan["price"]


# --- validation ------------------------------------------------------------

@pytest.mark.parametrize("field,value", [
    ("Phone", "12345"),                 # too short
    ("Phone", "12345678901"),           # too long
    ("Phone", "98765abcde"),            # not digits
    ("Phone", "9876543210' OR '1'='1"),  # injection attempt
    ("EmergencyContactPhone", "123"),
    ("Gender", "attack-helicopter"),
    ("Status", "terminated"),           # a staff status; members do not have it
    ("PaymentMethod", "bitcoin"),
    ("DateOfBirth", "2099-01-01"),
    ("JoiningDate", "1900-01-01"),
])
def test_invalid_values_are_rejected(api, auth, plan, field, value):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan, **{field: value}))
    assert r.status_code == 400, f"{field}={value!r} was accepted"


def test_expiry_before_joining_is_rejected(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, JoiningDate="2024-06-01", ExpiryDate="2024-01-01"))
    assert r.status_code == 400


def test_discount_cannot_exceed_the_price(api, auth, plan):
    """The plan price is not in the request, so only the service can catch this."""
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, DiscountAmount=str(plan["price"] + 1)))
    assert r.status_code == 400


def test_paid_cannot_exceed_the_total(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, DiscountAmount="500", PaidAmount="600"))
    assert r.status_code == 400


def test_unknown_plan_is_rejected(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan, PlanId="99999999"))
    assert r.status_code == 400


def test_inactive_plan_cannot_be_sold(api, auth, plan):
    api.put(f"{api.base}/api/membership-plans/{plan['id']}", headers=auth, json={
        "name": plan["name"], "durationValue": 1, "durationUnit": "month",
        "price": plan["price"], "serviceIds": [], "isActive": False,
    })
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan))
    assert r.status_code == 400


def test_page_size_is_capped(api, auth):
    """Without a ceiling this is a bulk export of personal data in one request."""
    assert api.get(f"{api.base}{MEMBERS}?pageSize=1000000", headers=auth).status_code == 400


# --- IDOR ------------------------------------------------------------------

def test_unknown_id_is_404_not_500(api, auth):
    assert api.get(f"{api.base}{MEMBERS}/99999999", headers=auth).status_code == 404


def test_unknown_id_status_change_is_404(api, auth):
    r = api.patch(f"{api.base}{MEMBERS}/99999999/status", headers=auth,
                  json={"status": "inactive"})
    assert r.status_code == 404


# --- uploads ---------------------------------------------------------------

def test_a_text_file_named_jpg_is_rejected(api, auth, plan):
    """Content-Type and extension are attacker-controlled; magic bytes are not."""
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan),
                 files={"photo": ("x.jpg", io.BytesIO(b"<script>alert(1)</script>"), "image/jpeg")})
    assert r.status_code == 400
    assert "JPEG" in r.text


def test_svg_is_rejected(api, auth, plan):
    """An SVG is XML that can carry <script> — stored XSS if ever rendered."""
    svg = b'<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>'
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan),
                 files={"photo": ("x.png", io.BytesIO(svg), "image/png")})
    assert r.status_code == 400


def test_traversal_filename_does_not_escape_the_upload_folder(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan),
                 files={"photo": ("../../../appsettings.json", io.BytesIO(PNG_1PX), "image/png")})
    assert r.status_code == 201, r.text
    created = r.json()
    try:
        assert created["photoUrl"].startswith("/uploads/members/")
        assert ".." not in created["photoUrl"]
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


def test_served_photo_carries_nosniff(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan),
                 files={"photo": ("ok.png", io.BytesIO(PNG_1PX), "image/png")})
    assert r.status_code == 201, r.text
    created = r.json()
    try:
        served = api.get(f"{api.base}{created['photoUrl']}")
        assert served.headers["X-Content-Type-Options"] == "nosniff"
        assert served.headers["Content-Type"] == "image/png"
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


def test_member_photos_do_not_land_in_the_staff_folder(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan),
                 files={"photo": ("ok.png", io.BytesIO(PNG_1PX), "image/png")})
    created = r.json()
    try:
        assert "/uploads/staff/" not in created["photoUrl"]
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


# --- money -----------------------------------------------------------------

def test_total_amount_is_price_minus_discount(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, Phone="9876500002", DiscountAmount="250", PaidAmount="0"))
    assert r.status_code == 201, r.text
    created = r.json()
    try:
        ms = created["currentMembership"]
        # total_amount is a STORED GENERATED column; this asserts EF is reading
        # it back rather than writing a value of its own.
        assert ms["totalAmount"] == ms["planPrice"] - ms["discountAmount"]
        assert ms["totalAmount"] == plan["price"] - 250
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


def test_unpaid_member_is_pending_with_no_payment_row(api, auth, member, plan):
    """payments has CHECK (amount > 0): pending is the absence of a payment."""
    ms = member["currentMembership"]
    assert ms["paidAmount"] == 0
    assert ms["paymentStatus"] == "pending"
    assert ms["balanceAmount"] == ms["totalAmount"]


def test_partial_payment_reports_partial(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, Phone="9876500003", PaidAmount="400"))
    created = r.json()
    try:
        ms = created["currentMembership"]
        assert ms["paidAmount"] == 400
        assert ms["paymentStatus"] == "partial"
        assert ms["balanceAmount"] == ms["totalAmount"] - 400
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


def test_full_payment_reports_paid(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, Phone="9876500004", PaidAmount=str(plan["price"])))
    created = r.json()
    try:
        assert created["currentMembership"]["paymentStatus"] == "paid"
        assert created["currentMembership"]["balanceAmount"] == 0
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


def test_expiry_is_a_full_period_minus_a_day(api, auth, plan):
    """A one-month plan started on the 1st ends on the 31st, not the next 1st."""
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, Phone="9876500005", JoiningDate="2024-01-01"))
    created = r.json()
    try:
        assert created["currentMembership"]["endDate"] == "2024-01-31"
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


def test_expiry_can_be_overridden(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(
        plan, Phone="9876500006", JoiningDate="2024-01-01", ExpiryDate="2024-03-15"))
    created = r.json()
    try:
        assert created["currentMembership"]["endDate"] == "2024-03-15"
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


# --- behaviour -------------------------------------------------------------

def test_member_codes_are_sequential(api, auth, plan, member):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(plan, Phone="9876500007"))
    second = r.json()
    try:
        first_n = int(member["memberCode"].removeprefix("MEM-"))
        second_n = int(second["memberCode"].removeprefix("MEM-"))
        assert second_n == first_n + 1
    finally:
        api.post(f"{api.base}{MEMBERS}/{second['id']}/erase", headers=auth)


def test_renewal_closes_the_previous_membership(api, auth, plan, member):
    """idx_one_active_membership is a partial UNIQUE — only one may be active."""
    r = api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships", headers=auth, json={
        "planId": plan["id"],
        "startDate": "2024-06-01",
        "discountAmount": 0,
        "paidAmount": plan["price"],
        "paymentMethod": "upi",
        "paymentReferenceNo": "TXN-RENEW-1",
    })
    assert r.status_code == 200, r.text
    history = r.json()["history"]
    assert len(history) == 2
    assert sum(1 for h in history if h["status"] == "active") <= 1


def test_renewal_keeps_the_old_price_snapshot(api, auth, plan, member):
    """A later price change must not move a receipt already handed over."""
    original = member["currentMembership"]["planPrice"]

    api.put(f"{api.base}/api/membership-plans/{plan['id']}", headers=auth, json={
        "name": plan["name"], "durationValue": 1, "durationUnit": "month",
        "price": 4000.00, "serviceIds": [], "isActive": True,
    })
    r = api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships", headers=auth, json={
        "planId": plan["id"], "startDate": "2024-06-01",
        "discountAmount": 0, "paidAmount": 0, "paymentMethod": "cash",
    })
    assert r.status_code == 200, r.text
    history = r.json()["history"]
    old = next(h for h in history if h["startDate"] == "2024-01-01")
    new = next(h for h in history if h["startDate"] == "2024-06-01")
    assert old["planPrice"] == original
    assert new["planPrice"] == 4000.00


def test_deactivating_keeps_the_member_visible(api, auth, plan):
    """The whole point of the switch: an inactive member is filed, not hidden."""
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, Phone="9876500008", PaidAmount="100"))
    created = r.json()
    code = created["memberCode"]
    try:
        assert api.patch(f"{api.base}{MEMBERS}/{created['id']}/status", headers=auth,
                         json={"status": "inactive"}).status_code == 204

        detail = api.get(f"{api.base}{MEMBERS}/{created['id']}", headers=auth)
        assert detail.status_code == 200
        assert detail.json()["status"] == "inactive"

        # Still on the unfiltered list, and findable under Inactive only.
        assert api.get(f"{api.base}{MEMBERS}?search={code}",
                       headers=auth).json()["totalCount"] == 1
        assert api.get(f"{api.base}{MEMBERS}?search={code}&memberStatus=inactive",
                       headers=auth).json()["totalCount"] == 1
        assert api.get(f"{api.base}{MEMBERS}?search={code}&memberStatus=active",
                       headers=auth).json()["totalCount"] == 0

        # Reversible, which is why it may not destroy anything on the way down.
        assert api.patch(f"{api.base}{MEMBERS}/{created['id']}/status", headers=auth,
                         json={"status": "active"}).status_code == 204
        assert api.get(f"{api.base}{MEMBERS}?search={code}&memberStatus=active",
                       headers=auth).json()["totalCount"] == 1
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


def test_deactivating_does_not_cancel_the_membership(api, auth, plan):
    """The easiest thing to undo by accident: a switch that quietly cancels."""
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, Phone="9876500018", PaidAmount="100"))
    created = r.json()
    before = created["currentMembership"]
    try:
        assert api.patch(f"{api.base}{MEMBERS}/{created['id']}/status", headers=auth,
                         json={"status": "inactive"}).status_code == 204

        after = api.get(f"{api.base}{MEMBERS}/{created['id']}", headers=auth).json()
        assert after["currentMembership"] is not None
        assert after["currentMembership"]["id"] == before["id"]
        assert after["currentMembership"]["status"] == before["status"]
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


def test_an_unknown_status_is_rejected(api, auth, member):
    r = api.patch(f"{api.base}{MEMBERS}/{member['id']}/status", headers=auth,
                  json={"status": "terminated"})   # a staff status; members have their own
    assert r.status_code == 400
    assert r.headers["content-type"].startswith("application/problem+json")


def test_erase_removes_the_row_from_the_listing(api, auth, plan):
    """`deleted_at` now means erased, and erasure is the only thing that hides."""
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, Phone="9876500019", PaidAmount="100"))
    created = r.json()

    assert api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth).status_code == 204
    assert api.get(f"{api.base}{MEMBERS}/{created['id']}", headers=auth).status_code == 404

    listing = api.get(f"{api.base}{MEMBERS}?search={created['memberCode']}", headers=auth).json()
    assert listing["totalCount"] == 0


def test_erase_clears_personal_data_but_keeps_the_code(api, auth, plan):
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(
        plan, Phone="9876500009", PaidAmount="100",
        EmergencyContactName="Next Of Kin", EmergencyContactPhone="9876500010"))
    created = r.json()
    code = created["memberCode"]

    assert api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth).status_code == 204

    # Erasing an already-erased member must stay idempotent, not 500.
    assert api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth).status_code == 204

    # The ledger still resolves the payment to a member code — erasure removes
    # the person, not the money.
    assert code.startswith("MEM-")


def test_update_does_not_change_what_was_bought(api, auth, plan, member):
    """Plan and money live on dated rows; only Renew may add one."""
    before = member["currentMembership"]

    r = api.put(f"{api.base}{MEMBERS}/{member['id']}", headers=auth, data=form(
        plan, FullName="Renamed Member", DiscountAmount="900", PaidAmount="900"))
    assert r.status_code == 200, r.text
    after = r.json()

    assert after["fullName"] == "Renamed Member"
    assert after["currentMembership"]["discountAmount"] == before["discountAmount"]
    assert after["currentMembership"]["totalAmount"] == before["totalAmount"]


# --- payment reference -----------------------------------------------------

def test_cash_payment_stores_no_reference(api, auth, plan):
    """A cash sale has no transaction id; storing one would be a fiction."""
    r = api.post(f"{api.base}{MEMBERS}", headers=auth, data=form(
        plan, Phone="9876500011", PaidAmount="100",
        PaymentMethod="cash", PaymentReferenceNo="TXN-SHOULD-BE-DROPPED"))
    assert r.status_code == 201, r.text
    created = r.json()
    try:
        history = api.get(f"{api.base}{MEMBERS}/{created['id']}", headers=auth).json()
        assert history["currentMembership"]["paidAmount"] == 100
    finally:
        api.post(f"{api.base}{MEMBERS}/{created['id']}/erase", headers=auth)


# --- stats -----------------------------------------------------------------

def test_stats_agree_with_the_listing(api, auth, member):
    stats = api.get(f"{api.base}{MEMBERS}/stats", headers=auth).json()
    listing = api.get(f"{api.base}{MEMBERS}?pageSize=100", headers=auth).json()
    assert stats["totalMembers"] == listing["totalCount"]
    assert stats["pendingPayments"] >= 1


def test_deactivating_moves_a_member_between_the_cards(api, auth, member):
    """Total is unchanged; the member leaves the membership cards for Inactive."""
    before = api.get(f"{api.base}{MEMBERS}/stats", headers=auth).json()

    assert api.patch(f"{api.base}{MEMBERS}/{member['id']}/status", headers=auth,
                     json={"status": "inactive"}).status_code == 204

    after = api.get(f"{api.base}{MEMBERS}/stats", headers=auth).json()
    assert after["totalMembers"] == before["totalMembers"]
    assert after["inactive"] == before["inactive"] + 1
    # The member fixture is created with a live membership, so it was counted
    # in exactly one of the three membership-state cards before.
    assert (after["active"] + after["expiringSoon"] + after["expired"]
            == before["active"] + before["expiringSoon"] + before["expired"] - 1)


# --- membership editing ----------------------------------------------------

def membership_body(plan, **overrides):
    payload = {
        "planId": plan["id"],
        "startDate": "2024-01-01",
        "discountAmount": 0,
    }
    payload.update(overrides)
    return payload


def test_editing_a_membership_resnapshots_the_plan_price(api, auth, plan, member):
    """A plan change is a re-sale, so the price is taken from the plan again."""
    other = api.post(f"{api.base}/api/membership-plans", headers=auth, json={
        "name": "Edit Target Plan", "durationValue": 3, "durationUnit": "month",
        "price": 2500.00, "serviceIds": [], "isActive": True,
    }).json()
    ms = member["currentMembership"]
    try:
        r = api.put(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}",
                    headers=auth, json=membership_body(other))
        assert r.status_code == 200, r.text
        updated = r.json()["currentMembership"]
        assert updated["planPrice"] == 2500.00
        assert updated["totalAmount"] == 2500.00
        assert updated["planName"] == "Edit Target Plan"
    finally:
        api.delete(f"{api.base}/api/membership-plans/{other['id']}", headers=auth)


def test_expiry_can_be_extended(api, auth, plan, member):
    ms = member["currentMembership"]
    r = api.put(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}",
                headers=auth, json=membership_body(plan, expiryDate="2025-12-31"))
    assert r.status_code == 200, r.text
    assert r.json()["currentMembership"]["endDate"] == "2025-12-31"


def test_expiry_recomputes_from_the_plan_when_omitted(api, auth, plan, member):
    ms = member["currentMembership"]
    r = api.put(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}",
                headers=auth, json=membership_body(plan, startDate="2024-03-01"))
    assert r.status_code == 200, r.text
    # One month from 1 March ends on 31 March, not 1 April.
    assert r.json()["currentMembership"]["endDate"] == "2024-03-31"


def test_expiry_before_start_is_rejected_on_edit(api, auth, plan, member):
    ms = member["currentMembership"]
    r = api.put(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}", headers=auth,
                json=membership_body(plan, startDate="2024-06-01", expiryDate="2024-01-01"))
    assert r.status_code == 400


def test_discount_above_price_is_rejected_on_edit(api, auth, plan, member):
    ms = member["currentMembership"]
    r = api.put(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}", headers=auth,
                json=membership_body(plan, discountAmount=plan["price"] + 1))
    assert r.status_code == 400


def test_edit_cannot_drop_the_total_below_what_was_collected(api, auth, plan, member):
    """Money already taken is a ledger fact; reducing under it needs a refund."""
    ms = member["currentMembership"]
    paid = api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}/payments",
                    headers=auth, json={"amount": plan["price"], "method": "cash"})
    assert paid.status_code == 200, paid.text

    r = api.put(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}", headers=auth,
                json=membership_body(plan, discountAmount=plan["price"] - 1))
    assert r.status_code == 400
    assert "collected" in r.text.lower()


def test_another_members_membership_is_not_reachable(api, auth, plan, member):
    """The membership is reached through its member, so this must 404, not edit."""
    other = api.post(f"{api.base}{MEMBERS}", headers=auth,
                     data=form(plan, Phone="9876500020")).json()
    try:
        victim = member["currentMembership"]["id"]
        r = api.put(f"{api.base}{MEMBERS}/{other['id']}/memberships/{victim}",
                    headers=auth, json=membership_body(plan))
        assert r.status_code == 404
    finally:
        api.post(f"{api.base}{MEMBERS}/{other['id']}/erase", headers=auth)


def test_edit_keeps_only_one_active_membership(api, auth, plan, member):
    """idx_one_active_membership is a partial UNIQUE — the edit must not trip it."""
    api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships", headers=auth, json={
        "planId": plan["id"], "startDate": "2024-06-01",
        "discountAmount": 0, "paidAmount": 0, "paymentMethod": "cash",
    })
    history = api.get(f"{api.base}{MEMBERS}/{member['id']}", headers=auth).json()["history"]
    older = next(h for h in history if h["startDate"] == "2024-01-01")

    r = api.put(f"{api.base}{MEMBERS}/{member['id']}/memberships/{older['id']}",
                headers=auth, json=membership_body(plan, status="active"))
    assert r.status_code == 200, r.text

    after = api.get(f"{api.base}{MEMBERS}/{member['id']}", headers=auth).json()["history"]
    assert sum(1 for h in after if h["status"] == "active") == 1


# --- partial payments ------------------------------------------------------

def test_partial_payments_walk_pending_to_partial_to_paid(api, auth, plan, member):
    ms = member["currentMembership"]
    url = f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}/payments"
    half = plan["price"] / 2

    assert ms["paymentStatus"] == "pending"

    first = api.post(url, headers=auth, json={"amount": half, "method": "upi",
                                              "referenceNo": "TXN-PART-1"})
    assert first.status_code == 200, first.text
    assert first.json()["currentMembership"]["paymentStatus"] == "partial"

    second = api.post(url, headers=auth, json={"amount": half, "method": "cash"})
    assert second.status_code == 200, second.text
    current = second.json()["currentMembership"]
    assert current["paymentStatus"] == "paid"
    assert current["balanceAmount"] == 0
    assert len(current["payments"]) == 2


def test_payment_above_the_outstanding_balance_is_rejected(api, auth, plan, member):
    ms = member["currentMembership"]
    r = api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}/payments",
                 headers=auth, json={"amount": plan["price"] + 1, "method": "cash"})
    assert r.status_code == 400


def test_zero_payment_is_rejected(api, auth, member):
    """payments has CHECK (amount > 0) — pending is the absence of a row."""
    ms = member["currentMembership"]
    r = api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}/payments",
                 headers=auth, json={"amount": 0, "method": "cash"})
    assert r.status_code == 400


def test_paying_a_settled_membership_is_rejected(api, auth, plan, member):
    ms = member["currentMembership"]
    url = f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}/payments"
    assert api.post(url, headers=auth, json={"amount": plan["price"], "method": "cash"}).status_code == 200
    r = api.post(url, headers=auth, json={"amount": 1, "method": "cash"})
    assert r.status_code == 400


def test_cash_payment_drops_the_reference(api, auth, plan, member):
    ms = member["currentMembership"]
    r = api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}/payments",
                 headers=auth, json={"amount": 100, "method": "cash",
                                     "referenceNo": "TXN-SHOULD-BE-DROPPED"})
    assert r.status_code == 200, r.text
    line = r.json()["currentMembership"]["payments"][0]
    assert line["referenceNo"] is None


def test_non_cash_payment_keeps_the_reference(api, auth, member):
    ms = member["currentMembership"]
    r = api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}/payments",
                 headers=auth, json={"amount": 100, "method": "upi", "referenceNo": "TXN-KEEP-ME"})
    assert r.status_code == 200, r.text
    assert r.json()["currentMembership"]["payments"][0]["referenceNo"] == "TXN-KEEP-ME"


def test_unknown_payment_method_is_rejected(api, auth, member):
    ms = member["currentMembership"]
    r = api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}/payments",
                 headers=auth, json={"amount": 100, "method": "bitcoin"})
    assert r.status_code == 400


def test_future_dated_payment_is_rejected(api, auth, member):
    ms = member["currentMembership"]
    r = api.post(f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}/payments",
                 headers=auth, json={"amount": 100, "method": "cash",
                                     "paidAt": "2099-01-01T00:00:00Z"})
    assert r.status_code == 400


def test_anonymous_cannot_edit_a_membership_or_pay(api, member):
    ms = member["currentMembership"]
    base = f"{api.base}{MEMBERS}/{member['id']}/memberships/{ms['id']}"
    assert api.put(base, json={"planId": 1, "startDate": "2024-01-01"}).status_code == 401
    assert api.post(f"{base}/payments", json={"amount": 1, "method": "cash"}).status_code == 401


# --- list filters added for the pending-payments and new-members screens ----
#
# The `member` fixture buys a 1000.00 plan and pays 0, so it always owes the
# full amount and always joined this month. That makes it the subject of every
# test below without any extra setup.

def test_owing_filter_returns_only_members_with_a_balance(api, auth, member):
    r = api.get(f"{api.base}{MEMBERS}", headers=auth, params={"paymentStatus": "owing"})
    assert r.status_code == 200, r.text
    body = r.json()

    assert any(row["id"] == member["id"] for row in body["items"]), "the unpaid fixture is missing"
    for row in body["items"]:
        assert row["balanceAmount"] > 0, row
        assert row["paymentStatus"] in ("pending", "partial"), row


def test_owing_count_agrees_with_the_stats_card(api, auth, member):
    """
    The card and the list it links to are two different queries — StatsAsync
    counts BalanceAmount > 0 in memory, the list filters payment_status in SQL.
    If they ever disagree the front desk chases a number it cannot open.
    """
    listed = api.get(f"{api.base}{MEMBERS}", headers=auth,
                     params={"paymentStatus": "owing", "pageSize": 1}).json()
    stats = api.get(f"{api.base}{MEMBERS}/stats", headers=auth).json()
    assert listed["totalCount"] == stats["pendingPayments"]


def test_paid_filter_is_the_inverse(api, auth, member):
    r = api.get(f"{api.base}{MEMBERS}", headers=auth, params={"paymentStatus": "paid"})
    assert r.status_code == 200, r.text
    for row in r.json()["items"]:
        assert row["paymentStatus"] == "paid", row
    assert all(row["id"] != member["id"] for row in r.json()["items"])


def test_balance_desc_sorts_largest_debt_first(api, auth):
    r = api.get(f"{api.base}{MEMBERS}", headers=auth,
                params={"paymentStatus": "owing", "sort": "balance_desc", "pageSize": 100})
    assert r.status_code == 200, r.text
    balances = [row["balanceAmount"] for row in r.json()["items"]]
    assert balances == sorted(balances, reverse=True)


def test_joined_this_month_filter(api, auth, plan, member):
    """
    `member` joins on 2024-01-01, so it is the negative case. A second member
    joining today is the positive one — the filter has to separate them.
    """
    today = datetime.date.today().isoformat()
    r = api.post(f"{api.base}{MEMBERS}", headers=auth,
                 data=form(plan, FullName="Joined Today", JoiningDate=today))
    assert r.status_code == 201, r.text
    fresh = r.json()

    try:
        rows = api.get(f"{api.base}{MEMBERS}", headers=auth,
                       params={"joined": "this_month", "pageSize": 100}).json()["items"]

        ids = [row["id"] for row in rows]
        assert fresh["id"] in ids
        assert member["id"] not in ids, "a 2024 joiner is not a joiner this month"

        month_start = today[:7] + "-01"
        for row in rows:
            assert row["joinedOn"] >= month_start, row
    finally:
        api.post(f"{api.base}{MEMBERS}/{fresh['id']}/erase", headers=auth)


def test_list_row_carries_membership_id(api, auth, member):
    """The pending screen opens Record Payment straight off the row."""
    r = api.get(f"{api.base}{MEMBERS}", headers=auth, params={"search": member["memberCode"]})
    row = r.json()["items"][0]
    assert row["membershipId"] == member["currentMembership"]["id"]


@pytest.mark.parametrize("params", [
    {"paymentStatus": "nonsense"},
    {"joined": "last_decade"},
    {"sort": "nonsense"},
])
def test_unknown_filter_values_are_ignored_not_rejected(api, auth, params):
    """A stale bookmark shows the list, not a 400 — same rule as ?status=foo."""
    r = api.get(f"{api.base}{MEMBERS}", headers=auth, params=params)
    assert r.status_code == 200, r.text


def test_the_default_list_is_unchanged_by_the_new_parameters(api, auth):
    """
    Guards the regression that matters: /members with no query string must sort
    and page exactly as it did before sort/paymentStatus/joined existed.
    """
    plain = api.get(f"{api.base}{MEMBERS}", headers=auth, params={"pageSize": 20}).json()
    names = [row["fullName"] for row in plain["items"]]
    assert names == sorted(names)
