"""Every failure leaves as one problem+json shape.

The SPA has a single error path: `normalizeError` in services/api.ts folds the
response into one message string. That only works while every failure — thrown
exception, model-validation 400, bodiless 401 from the fallback policy,
unmatched route — carries `errors` in a shape it understands. These tests are
what stops a future endpoint from inventing a second format.
"""
import pytest

from conftest import OWNER_EMAIL


def problem(response):
    """Assert the envelope and return it."""
    body = response.json()
    assert body["status"] == response.status_code
    assert isinstance(body["title"], str) and body["title"]
    assert isinstance(body["code"], str) and body["code"]
    assert isinstance(body["traceId"], str) and body["traceId"]
    assert "errors" in body
    return body


def messages(body):
    """`errors` is either a list of strings or a field -> messages map."""
    errors = body["errors"]
    if isinstance(errors, dict):
        return [m for group in errors.values() for m in group]
    return list(errors)


@pytest.fixture
def auth(tokens):
    return {"Authorization": f"Bearer {tokens['accessToken']}"}


# ---------------------------------------------------------------------------
# The four ways a request can fail
# ---------------------------------------------------------------------------

def test_unmatched_route_is_a_problem_document(api, auth):
    """A 404 from routing, before any controller runs."""
    r = api.get(f"{api.base}/api/does-not-exist", headers=auth)
    assert r.status_code == 404
    assert problem(r)["code"] == "not_found"


def test_unknown_route_is_401_before_it_is_404(api):
    """No endpoint matched, so there is no [AllowAnonymous] to find, and the
    FallbackPolicy answers 401. That ordering is worth keeping: an anonymous
    caller cannot map which routes exist by telling 404 from 401."""
    r = api.get(f"{api.base}/api/does-not-exist")
    assert r.status_code == 401
    assert problem(r)["code"] == "unauthorized"


def test_missing_token_is_a_problem_document(api):
    """The fallback policy's 401 has no body of its own — UseStatusCodePages
    gives it one."""
    r = api.get(f"{api.base}/api/receptionists")
    assert r.status_code == 401
    assert problem(r)["code"] == "unauthorized"


def test_model_validation_is_a_problem_document(api, auth):
    """[ApiController]'s automatic 400, re-routed through ApiProblem."""
    r = api.post(f"{api.base}/api/membership-plans", headers=auth,
                 json={"name": "", "price": -5})
    assert r.status_code == 400
    body = problem(r)
    assert body["code"] == "validation_failed"
    assert messages(body), "a validation failure must say which fields are wrong"


def test_unparseable_body_is_400_not_500(api, auth):
    """A malformed JSON body raises inside the input formatter, not the
    controller — it must still come back as the same shape."""
    r = api.post(f"{api.base}/api/membership-plans",
                 headers={**auth, "Content-Type": "application/json"},
                 data="{not json")
    assert r.status_code == 400
    problem(r)


def test_controller_notfound_is_a_problem_document(api, auth):
    """A bare NotFound() from a controller never passes through ApiProblem, so
    MVC would write a document with no `code` and no `errors` — leaving the
    SPA's normalizeError with nothing to render. CustomizeProblemDetails fills
    those in, which is what keeps the contract true for call sites nobody
    remembers to route through the factory."""
    r = api.get(f"{api.base}/api/receptionists/999999", headers=auth)
    assert r.status_code == 404
    body = problem(r)
    assert body["code"] == "not_found"
    assert messages(body)


def test_wrong_method_is_not_labelled_a_validation_error(api, auth):
    """405 is about the verb, not the body. Labelling it `validation_failed`
    sends a client looking for a bad field that does not exist."""
    r = api.patch(f"{api.base}/api/receptionists", headers=auth)
    assert r.status_code == 405
    assert problem(r)["code"] == "method_not_allowed"


def test_oversized_upload_is_413_and_does_not_quote_the_limit(api, auth):
    """Model binding files the transport failure as a model error, which turns
    a 413 into a 400 whose message quotes the exact byte ceiling. Both halves
    of that matter: the status has to be right, and the ceiling is not the
    caller's business — publishing it is a free hint for anyone probing for a
    request-size denial of service."""
    oversized = b"\x00" * (5 * 1024 * 1024)
    r = api.post(f"{api.base}/api/receptionists", headers=auth,
                 data={"fullName": "Too Big"},
                 files={"photo": ("big.bin", oversized, "application/octet-stream")})
    assert r.status_code == 413
    body = problem(r)
    assert body["code"] == "payload_too_large"
    assert "4194304" not in r.text and "max request body size" not in r.text.lower()


# ---------------------------------------------------------------------------
# What must never appear in a response
# ---------------------------------------------------------------------------

def test_traceid_is_echoed_as_a_header(api):
    """The id on the error banner has to be findable in the logs."""
    r = api.get(f"{api.base}/api/receptionists")
    assert r.headers.get("X-Trace-Id")
    assert r.json()["traceId"] == r.headers["X-Trace-Id"]


def test_errors_never_carry_a_stack_trace(api, auth):
    """A leaked stack trace hands an attacker the framework version, the file
    layout and the call graph. `debugMessage` is Development-only and is not a
    stack trace; `detail` must never be one anywhere."""
    for response in (
        api.get(f"{api.base}/api/does-not-exist", headers=auth),
        api.get(f"{api.base}/api/receptionists"),
        api.get(f"{api.base}/api/receptionists/999999", headers=auth),
    ):
        text = response.text
        assert "at GymApis." not in text
        assert "StackTrace" not in text
        assert ".cs:line" not in text


def test_forgot_password_is_202_even_if_email_fails(api):
    """The uniform 202 is an anti-enumeration control. It has to survive a mail
    provider outage — a 502 for real addresses and a 202 for unknown ones would
    be the exact oracle the uniform response exists to close."""
    known = api.post(f"{api.base}/api/auth/forgot-password", json={"email": OWNER_EMAIL})
    unknown = api.post(f"{api.base}/api/auth/forgot-password",
                       json={"email": "nobody-at-all@steelflex.test"})
    assert known.status_code == unknown.status_code == 202
