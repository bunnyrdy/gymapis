"""Auth regression guards.

These cover the parts of the login module whose failure is silent and
expensive: token rotation, and the reuse detection that makes a stolen refresh
token useless. See 002_refresh_tokens.sql for the model being enforced.
"""
from conftest import OWNER_EMAIL, OWNER_PASSWORD


def test_login_returns_a_token_pair(tokens):
    assert tokens["accessToken"]
    assert tokens["refreshToken"]
    assert tokens["role"] == "owner"
    assert tokens["expiresInSeconds"] > 0


def test_login_with_wrong_password_is_401(api):
    r = api.post(f"{api.base}/api/auth/login",
                 json={"email": OWNER_EMAIL, "password": "definitely-not-it"})
    assert r.status_code == 401


def test_login_with_unknown_email_is_401_with_the_same_message(api):
    """Identical response to a wrong password — no account enumeration."""
    unknown = api.post(f"{api.base}/api/auth/login",
                       json={"email": "nobody@nowhere.test", "password": "x"})
    wrong = api.post(f"{api.base}/api/auth/login",
                     json={"email": OWNER_EMAIL, "password": "x"})
    assert unknown.status_code == wrong.status_code == 401
    assert unknown.json()["errors"] == wrong.json()["errors"]


def test_refresh_rotates_the_token(api, tokens):
    r = api.post(f"{api.base}/api/auth/refresh",
                 json={"refreshToken": tokens["refreshToken"]})
    assert r.status_code == 200, r.text
    assert r.json()["refreshToken"] != tokens["refreshToken"]


def test_replaying_a_rotated_token_revokes_the_whole_family(api, tokens):
    first = tokens["refreshToken"]
    second = api.post(f"{api.base}/api/auth/refresh",
                      json={"refreshToken": first}).json()["refreshToken"]

    # Replaying `first` means it was copied: reuse detected.
    assert api.post(f"{api.base}/api/auth/refresh",
                    json={"refreshToken": first}).status_code == 401

    # ...and the live token from that same login dies with it.
    assert api.post(f"{api.base}/api/auth/refresh",
                    json={"refreshToken": second}).status_code == 401


def test_logout_kills_the_refresh_token(api, tokens):
    assert api.post(f"{api.base}/api/auth/logout",
                    json={"refreshToken": tokens["refreshToken"]}).status_code == 204
    assert api.post(f"{api.base}/api/auth/refresh",
                    json={"refreshToken": tokens["refreshToken"]}).status_code == 401


def test_forgot_password_is_202_for_unknown_addresses(api):
    """Same answer either way, or the endpoint becomes an enumeration oracle."""
    known = api.post(f"{api.base}/api/auth/forgot-password", json={"email": OWNER_EMAIL})
    unknown = api.post(f"{api.base}/api/auth/forgot-password",
                       json={"email": "nobody@nowhere.test"})
    assert known.status_code == unknown.status_code == 202


def test_reset_with_a_bogus_token_is_rejected(api):
    r = api.post(f"{api.base}/api/auth/reset-password",
                 json={"token": "not-a-real-token", "newPassword": "Whatever@123"})
    assert r.status_code == 400


def test_owner_password_still_works(api):
    """Canary: the suite above must leave the seeded credentials intact."""
    r = api.post(f"{api.base}/api/auth/login",
                 json={"email": OWNER_EMAIL, "password": OWNER_PASSWORD})
    assert r.status_code == 200
