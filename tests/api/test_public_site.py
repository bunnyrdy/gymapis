"""Public website + CMS guards.

This module is the first anonymous surface in the API besides sign-in, so the
tests lean on the controls whose failure is silent rather than loud:

  * the public endpoints must answer WITHOUT a token — the fallback policy is
    fail-closed, and a missing [AllowAnonymous] would 401 every visitor;
  * the CMS must answer 401 without one;
  * an unpublished row must not leak, and a transformation without recorded
    consent must not leak even when someone flips is_active by hand;
  * the public payload must not carry internal fields (member ids, consent
    timestamps, plan codes);
  * uploads are still sniffed by magic bytes, and the page-size ceilings hold.

Everything created here cleans itself up.
"""
import io
import struct
import zlib

import pytest

PUBLIC = "/api/public"
SITE = "/api/site"


@pytest.fixture
def auth(tokens):
    return {"Authorization": f"Bearer {tokens['accessToken']}"}


@pytest.fixture
def settings_restored(api, auth):
    """Put the settings row back the way it was found.

    `PUT /api/site/settings` is a full replace, which is correct — the CMS form
    posts every field every time. It means a test that posts a partial body to
    check one rule wipes the rest of the row, and the tests below do exactly
    that. Without this fixture, running the suite silently blanks the live
    website's hero copy, founded year and social links. That was found the hard
    way, an hour before a demo.
    """
    before = api.get(f"{api.base}{SITE}/settings", headers=auth).json()
    yield before

    # Listed explicitly rather than filtered out of the response: the write DTO
    # is an allow-list and this has to match it exactly. Anything sent that the
    # DTO does not bind is ignored, and anything omitted is blanked.
    api.put(f"{api.base}{SITE}/settings", headers=auth, json={
        field: before.get(field) for field in (
            "heroHeadline", "heroSubtext", "foundedYear", "memberCountOverride",
            "aboutTitle", "aboutDescription", "instagramUrl", "whatsappNumber",
            "mapsUrl", "footerText",
        )
    })


def png_bytes(colour=(200, 80, 40)):
    """A real 4x4 PNG. Magic-byte validation is the point, so this cannot be a stub."""
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    raw = b"".join(b"\x00" + bytes(colour) * 4 for _ in range(4))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", 4, 4, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw))
            + chunk(b"IEND", b""))


def photo(name="photo.png"):
    return (name, io.BytesIO(png_bytes()), "image/png")


# ---------------------------------------------------------------------------
# Anonymous access — both directions
# ---------------------------------------------------------------------------

def test_public_site_is_readable_anonymously(api):
    """No token. If the fallback policy ever swallows this, every visitor sees a 401."""
    r = api.get(f"{api.base}{PUBLIC}")
    assert r.status_code == 200, r.text

    body = r.json()
    for key in ("gymName", "hero", "plans", "offers", "transformations", "events", "contact"):
        assert key in body, f"public payload lost {key}"


def test_public_paged_lists_are_readable_anonymously(api):
    for path in ("gallery", "transformations", "events"):
        r = api.get(f"{api.base}{PUBLIC}/{path}")
        assert r.status_code == 200, f"{path}: {r.text}"
        assert "items" in r.json()


@pytest.mark.parametrize("path", ["settings", "media", "offers", "transformations", "events"])
def test_cms_rejects_anonymous(api, path):
    r = api.get(f"{api.base}{SITE}/{path}")
    assert r.status_code == 401, r.text


def test_cms_write_rejects_anonymous(api):
    r = api.post(f"{api.base}{SITE}/offers", json={"title": "Free month"})
    assert r.status_code == 401, r.text


# ---------------------------------------------------------------------------
# The hero falls back rather than failing
# ---------------------------------------------------------------------------

def test_hero_always_has_a_headline(api):
    """Nullable columns everywhere, so an unconfigured site must still render."""
    hero = api.get(f"{api.base}{PUBLIC}").json()["hero"]
    assert hero["headline"], "hero headline should fall back to the tenant name"
    assert isinstance(hero["activeMembers"], int)


def test_years_of_experience_is_derived_not_stored(api, auth, settings_restored):
    """founded_year is stored; the years figure is computed, so it cannot go stale."""
    r = api.put(f"{api.base}{SITE}/settings", headers=auth, json={"foundedYear": 2019})
    assert r.status_code == 200, r.text

    hero = api.get(f"{api.base}{PUBLIC}").json()["hero"]
    assert hero["yearsOfExperience"] is not None
    assert hero["yearsOfExperience"] >= 6


def test_settings_rejects_a_javascript_url(api, auth, settings_restored):
    """These land in an href on a page anyone can visit — a javascript: URL is stored XSS."""
    r = api.put(f"{api.base}{SITE}/settings", headers=auth,
                json={"instagramUrl": "javascript:alert(1)"})
    assert r.status_code == 400, r.text


def test_settings_does_not_accept_artwork_urls(api, auth, settings_restored):
    """Artwork is uploaded, never named. A settable URL points the homepage anywhere."""
    r = api.put(f"{api.base}{SITE}/settings", headers=auth,
                json={"heroImageUrl": "https://evil.example/x.png"})
    assert r.status_code == 200, r.text
    assert r.json()["heroImageUrl"] != "https://evil.example/x.png"


# ---------------------------------------------------------------------------
# Drafts stay off the internet
# ---------------------------------------------------------------------------

def test_inactive_offer_is_hidden_from_the_public(api, auth):
    created = api.post(f"{api.base}{SITE}/offers", headers=auth,
                       json={"title": "Draft offer", "isActive": False})
    assert created.status_code == 201, created.text
    offer_id = created.json()["id"]

    try:
        titles = [o["title"] for o in api.get(f"{api.base}{PUBLIC}").json()["offers"]]
        assert "Draft offer" not in titles
    finally:
        api.delete(f"{api.base}{SITE}/offers/{offer_id}", headers=auth)


def test_inactive_media_is_hidden_from_the_public(api, auth):
    created = api.post(
        f"{api.base}{SITE}/media", headers=auth,
        data={"Section": "gallery", "Caption": "Hidden shot", "IsActive": "false"},
        files={"file": photo()})
    assert created.status_code == 200, created.text
    media_id = created.json()["id"]

    try:
        captions = [m["caption"] for m in api.get(f"{api.base}{PUBLIC}/gallery").json()["items"]]
        assert "Hidden shot" not in captions
    finally:
        api.delete(f"{api.base}{SITE}/media/{media_id}", headers=auth)


# ---------------------------------------------------------------------------
# DPDP: consent gates publication, and withdrawing it takes the story down
# ---------------------------------------------------------------------------

def transformation_form(**overrides):
    form = {"DisplayName": "Test Subject", "Goal": "Muscle Gain", "IsActive": "true",
            "ConsentGiven": "true"}
    form.update(overrides)
    return form


@pytest.fixture
def transformation(api, auth):
    r = api.post(f"{api.base}{SITE}/transformations", headers=auth,
                 data=transformation_form(),
                 files={"before": photo("before.png"), "after": photo("after.png")})
    assert r.status_code == 201, r.text
    row = r.json()
    yield row
    api.delete(f"{api.base}{SITE}/transformations/{row['id']}", headers=auth)


def test_publishing_without_consent_is_rejected(api, auth):
    r = api.post(f"{api.base}{SITE}/transformations", headers=auth,
                 data=transformation_form(ConsentGiven="false"),
                 files={"before": photo("before.png"), "after": photo("after.png")})
    assert r.status_code == 400, r.text


def test_consent_is_stamped_by_the_server(api, transformation):
    """The client sends a tick, not a date. A back-dated consent must be impossible."""
    assert transformation["consentGivenAt"] is not None
    assert transformation["isActive"] is True


def test_withdrawing_consent_unpublishes_the_story(api, auth, transformation):
    before = api.get(f"{api.base}{PUBLIC}/transformations").json()["totalCount"]
    assert before >= 1

    r = api.put(f"{api.base}{SITE}/transformations/{transformation['id']}", headers=auth,
                data=transformation_form(IsActive="false", ConsentGiven="false"))
    assert r.status_code == 200, r.text
    assert r.json()["consentGivenAt"] is None
    assert r.json()["isActive"] is False

    after = api.get(f"{api.base}{PUBLIC}/transformations").json()["totalCount"]
    assert after == before - 1


def test_a_transformation_needs_both_photos(api, auth):
    r = api.post(f"{api.base}{SITE}/transformations", headers=auth,
                 data=transformation_form(),
                 files={"before": photo("before.png")})
    assert r.status_code == 400, r.text


def test_unknown_member_id_is_400_not_500(api, auth):
    r = api.post(f"{api.base}{SITE}/transformations", headers=auth,
                 data=transformation_form(MemberId="99999999"),
                 files={"before": photo("before.png"), "after": photo("after.png")})
    assert r.status_code == 400, r.text


def test_public_transformation_carries_no_internal_fields(api, transformation):
    items = api.get(f"{api.base}{PUBLIC}/transformations").json()["items"]
    assert items, "the consented fixture should be visible"

    leaked = {"memberId", "consentGivenAt", "consentCapturedBy", "isActive", "id", "displayOrder"}
    assert not leaked & set(items[0]), f"public payload leaks {leaked & set(items[0])}"


def test_public_plan_carries_no_plan_code(api):
    for plan in api.get(f"{api.base}{PUBLIC}").json()["plans"]:
        assert "planCode" not in plan, "the internal plan code is not for visitors"


# ---------------------------------------------------------------------------
# Uploads
# ---------------------------------------------------------------------------

def test_media_rejects_a_file_that_is_not_an_image_or_video(api, auth):
    """Magic bytes, not the filename or the Content-Type — both are attacker-controlled."""
    fake = ("clip.mp4", io.BytesIO(b"NOT A VIDEO" * 40), "video/mp4")
    r = api.post(f"{api.base}{SITE}/media", headers=auth,
                 data={"Section": "gallery"}, files={"file": fake})
    assert r.status_code == 400, r.text


def test_media_rejects_svg(api, auth):
    """SVG is XML that can carry <script>. Same rule as every other upload here."""
    svg = ("x.svg", io.BytesIO(b"<svg xmlns='http://www.w3.org/2000/svg'><script/></svg>"),
           "image/svg+xml")
    r = api.post(f"{api.base}{SITE}/media", headers=auth,
                 data={"Section": "gallery"}, files={"file": svg})
    assert r.status_code == 400, r.text


def test_media_rejects_an_unknown_section(api, auth):
    r = api.post(f"{api.base}{SITE}/media", headers=auth,
                 data={"Section": "../../etc"}, files={"file": photo()})
    assert r.status_code == 400, r.text


# ---------------------------------------------------------------------------
# Reorder
# ---------------------------------------------------------------------------

def test_reorder_rejects_a_foreign_id_400_not_500(api, auth):
    r = api.patch(f"{api.base}{SITE}/offers/reorder", headers=auth,
                  json={"orderedIds": [99999999]})
    assert r.status_code == 400, r.text


def test_reorder_rejects_an_unknown_content_type(api, auth):
    r = api.patch(f"{api.base}{SITE}/members/reorder", headers=auth,
                  json={"orderedIds": [1]})
    assert r.status_code == 400, r.text


def test_reorder_rejects_duplicate_ids(api, auth):
    r = api.patch(f"{api.base}{SITE}/offers/reorder", headers=auth,
                  json={"orderedIds": [1, 1]})
    assert r.status_code == 400, r.text


def test_reorder_applies_the_order(api, auth):
    ids = []
    try:
        for name in ("Reorder A", "Reorder B"):
            r = api.post(f"{api.base}{SITE}/offers", headers=auth,
                         json={"title": name, "isActive": True})
            assert r.status_code == 201, r.text
            ids.append(r.json()["id"])

        r = api.patch(f"{api.base}{SITE}/offers/reorder", headers=auth,
                      json={"orderedIds": list(reversed(ids))})
        assert r.status_code == 204, r.text

        rows = {o["id"]: o["displayOrder"]
                for o in api.get(f"{api.base}{SITE}/offers", headers=auth).json()["items"]}
        assert rows[ids[1]] < rows[ids[0]]
    finally:
        for offer_id in ids:
            api.delete(f"{api.base}{SITE}/offers/{offer_id}", headers=auth)


# ---------------------------------------------------------------------------
# Ceilings
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("path", ["gallery", "transformations", "events"])
def test_public_page_size_is_capped(api, path):
    """Without a ceiling, ?pageSize=1000000 is a bulk export and a DoS in one request."""
    r = api.get(f"{api.base}{PUBLIC}/{path}", params={"pageSize": 1000})
    assert r.status_code == 400, r.text


def test_cms_page_size_is_capped(api, auth):
    r = api.get(f"{api.base}{SITE}/offers", headers=auth, params={"pageSize": 1000})
    assert r.status_code == 400, r.text


# ---------------------------------------------------------------------------
# 404s stay 404s
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("path", ["media", "offers", "transformations", "events"])
def test_unknown_id_is_404(api, auth, path):
    r = api.delete(f"{api.base}{SITE}/{path}/99999999", headers=auth)
    assert r.status_code == 404, r.text
