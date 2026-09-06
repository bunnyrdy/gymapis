#!/usr/bin/env python3
"""Fill the public website with demo content.

    python3 scripts/seed_demo_site.py           # clear, then seed
    python3 scripts/seed_demo_site.py --clear   # clear only

WHY THIS TALKS TO THE API AND NOT TO POSTGRES

Every image here is uploaded through `POST /api/site/...`, so it passes the real
magic-byte validation, lands in wwwroot via PhotoStorage with a generated
filename, and every row is written by the same service the CMS screens use. A
seed that wrote to the database directly would put content on the site that the
application had never agreed to accept, and would prove nothing about the module
working.

    ############################################################
    #  THE TRANSFORMATIONS SEEDED HERE ARE FABRICATED.
    #
    #  They are before/after testimonials attributed to named
    #  people, and this script stamps consent on them so they
    #  publish. That consent is not real. It is fine in a demo
    #  database and is a DPDP problem in a live one.
    #
    #  Run with --clear before this database is used for
    #  anything real.
    ############################################################

Assets are cached under scripts/.demo-assets/ so a re-run does not pull ~30 MB
again. Every download is checked against its magic bytes and skipped with a
warning if it fails, so one dead stock-photo id cannot abort the seed on the
morning of a demo.
"""

import argparse
import os
import sys
import urllib.request
import urllib.error
import uuid
from datetime import date, timedelta
from pathlib import Path

try:
    import requests
except ImportError:
    sys.exit("This script needs `requests`: pip install -r gymapis/tests/api/requirements.txt")


BASE_URL = os.environ.get("GYMAPI_BASE_URL", "http://localhost:5023")
OWNER_EMAIL = os.environ.get("GYMAPI_OWNER_EMAIL", "admin@steelflex.com")
OWNER_PASSWORD = os.environ.get("GYMAPI_OWNER_PASSWORD", "SteelFlex@123")

REPO = Path(__file__).resolve().parent.parent
CACHE = Path(__file__).resolve().parent / ".demo-assets"

# Browsers only. Both hosts refuse a bare urllib/curl user agent.
UA = ("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

# Magic bytes, matching PhotoStorage. Checked here so a truncated or
# 404-HTML download is caught before it becomes a 400 from the API.
SIGNATURES = {
    "jpg":  (0, b"\xff\xd8\xff"),
    "png":  (0, b"\x89PNG\r\n\x1a\n"),
    "webp": (0, b"RIFF"),
    "mp4":  (4, b"ftyp"),
}


# ---------------------------------------------------------------------------
# The asset catalogue
# ---------------------------------------------------------------------------

# Real photographs of the client's gym in Medchal, already in the repo. These go
# where the owner will recognise their own floor: the About collage and the
# gallery. Stock fills everything that needs people or more frames.
REAL_GYM = [
    REPO / "steelflex gym medchel - Google Search_files" / "unnamed(30).webp",
    REPO / "steelflex gym medchel - Google Search_files" / "unnamed.webp",
    REPO / "steelflex gym medchel - Google Search_files" / "unnamed(31).webp",
    REPO / "steelflex gym medchel - Google Search_files" / "unnamed(29).webp",
]


def unsplash(photo_id: str, w: int = 1600) -> str:
    return f"https://images.unsplash.com/{photo_id}?w={w}&q=80&fm=jpg&fit=crop"


HERO_IMAGE = unsplash("photo-1517836357463-d25dfeac3438", 2000)
LOGIN_IMAGE = unsplash("photo-1534438327276-14e5300c3a48", 1400)

GALLERY_PHOTOS = [
    ("photo-1534438327276-14e5300c3a48", "The main floor"),
    ("photo-1571019613454-1cb2f99b2d8b", "Strength zone"),
    ("photo-1540497077202-7c8a3999166f", "Free weights"),
    ("photo-1581009146145-b5ef050c2e1e", "Functional training"),
    ("photo-1550345332-09e3ac987658", "Cardio deck"),
    ("photo-1518611012118-696072aa579a", "Heavy lifting"),
    ("photo-1526506118085-60ce8714f8c5", "Conditioning"),
    ("photo-1532384748853-8f54a8f476e2", "Group classes"),
    ("photo-1583454110551-21f2fa2afe61", "Coaching on the floor"),
]

# Pexels HD. Kept to HD rather than UHD so each file lands around 4 MB, well
# under the 32 MB ceiling in PhotoStorage.SaveVideoAsync.
# Each of these was reachability-checked before being listed. Pexels 403s most
# of its catalogue to a non-browser client even with a UA and Referer, so do not
# add an id here without confirming it downloads first.
VIDEOS = {
    "hero": "https://videos.pexels.com/video-files/6389062/6389062-hd_1920_1080_25fps.mp4",
    "gallery1": "https://videos.pexels.com/video-files/5319755/5319755-hd_1920_1080_25fps.mp4",
    "gallery2": "https://videos.pexels.com/video-files/3195394/3195394-hd_1920_1080_25fps.mp4",
}

# Before/after pairs are the one place stock photography fights back: there is
# no such thing as a genuine same-person before/after in a generic library. The
# pairs below are therefore chosen to be *consistent* — same apparent person
# type in both frames — so the comparison reads at a glance instead of showing a
# woman on the left and a man on the right. They are still fabricated; see the
# banner at the top of this file.
TRANSFORMATIONS = [
    {
        "name": "Rahul Sharma", "goal": "Muscle Gain",
        "achievement": "Gained 8 kg of lean muscle", "duration": "6 Months",
        "description": "Started with no lifting background. Structured progressive overload "
                       "four days a week, with the diet plan reviewed every fortnight.",
        "before": "photo-1517838277536-f5f99be501cd", "after": "photo-1567013127542-490d757e51fc",
    },
    {
        "name": "Priya Singh", "goal": "Weight Loss",
        "achievement": "Lost 15 kg of fat", "duration": "4 Months",
        "description": "Combined steady-state cardio with three strength sessions a week. "
                       "The nutrition side did most of the work.",
        "before": "photo-1546483875-ad9014c88eba", "after": "photo-1550259979-ed79b48d2a30",
    },
    {
        "name": "David Chen", "goal": "Athletic Performance",
        "achievement": "Doubled core strength", "duration": "8 Months",
        "description": "Sport-specific conditioning built around mobility work and "
                       "explosive compound lifts.",
        "before": "photo-1519505907962-0a6cb0167c73", "after": "photo-1581009137042-c552e485697a",
    },
]

EVENTS = [
    {
        "title": "30-Day Kinetic Challenge", "days": 17,
        "start": "06:00", "end": "07:00", "location": "Main Floor",
        "description": "Thirty days, one session a day, a leaderboard on the wall. "
                       "Open to every member at no extra cost.",
        "image": "photo-1552674605-db6ffd4facb5",
    },
    {
        "title": "Mobility & Recovery Workshop", "days": 24,
        "start": "10:00", "end": "12:30", "location": "Studio 2",
        "description": "A half-day on the things that keep you training: joint prep, "
                       "loaded stretching and how to program a deload.",
        "image": "photo-1544367567-0f2fcb009e0b",
    },
    {
        "title": "Strength Fundamentals Clinic", "days": 38,
        "start": "17:30", "end": "19:00", "location": "Strength Zone",
        "description": "Squat, bench, deadlift and overhead press taken apart and rebuilt, "
                       "with individual coaching on every rep.",
        "image": "photo-1571019613454-1cb2f99b2d8b",
    },
    {
        "title": "Community Endurance Ride", "days": 52,
        "start": "05:30", "end": "07:30", "location": "Cardio Deck",
        "description": "A two-hour group ride with pacing coaching. Bring water and a towel.",
        "image": "photo-1558611848-73f7eb4001a1",
    },
]

OFFERS = [
    {"title": "Join Today & Get 10% Off", "valueLabel": "10% OFF",
     "description": "Valid for new members signing up on an annual plan this month."},
    {"title": "Free Fitness Assessment", "valueLabel": "FREE",
     "description": "A full body-composition assessment and a consultation with one of our coaches."},
    {"title": "Refer a Friend, Get a Month", "valueLabel": "1 MONTH",
     "description": "Both of you get a free month added when your referral joins."},
]

PLANS = [
    {"name": "Basic", "durationValue": 1, "durationUnit": "month", "price": 999,
     "description": "Essential access for steady, consistent progress.",
     "isFeatured": False, "displayOrder": 1, "services": ["Gym Floor Access", "Cardio Training"]},
    {"name": "Standard", "durationValue": 3, "durationUnit": "month", "price": 2499,
     "description": "Expanded training options for committed members.",
     "isFeatured": False, "displayOrder": 2,
     "services": ["Gym Floor Access", "Strength Training", "Cardio Training", "Functional Training"]},
    {"name": "Premium", "durationValue": 6, "durationUnit": "month", "price": 4999,
     "description": "Comprehensive coaching and holistic progress tracking.",
     "isFeatured": True, "displayOrder": 3,
     "services": ["Gym Floor Access", "Strength Training", "Cardio Training",
                  "Functional Training", "Group Fitness Classes", "Diet / Nutrition Consultation"]},
    {"name": "Elite Annual", "durationValue": 12, "durationUnit": "month", "price": 9999,
     "description": "The full facility, personal training included.",
     "isFeatured": False, "displayOrder": 4,
     "services": ["Gym Floor Access", "Strength Training", "Cardio Training",
                  "Functional Training", "Group Fitness Classes", "Personal Training",
                  "Diet / Nutrition Consultation", "Locker Facility", "Shower Facility"]},
]

SETTINGS = {
    "heroHeadline": "Train Like It Matters",
    "heroSubtext": "Engineered excellence in Medchal. Premium equipment, certified coaches, "
                   "and an environment built for people who show up.",
    "foundedYear": 2019,
    # The dev database has four real members. The hero should not publish that at
    # a client demo. Clear this field to go back to the live count.
    "memberCountOverride": 250,
    "aboutTitle": "More Than a Gym — A Place to Transform Your Lifestyle",
    "aboutDescription": (
        "At Steel Flex we believe in engineered excellence. The facility is designed to "
        "remove distractions and focus entirely on your progression: top-tier equipment, "
        "coaches who know what they are doing, and an environment that keeps momentum going "
        "every time you step on the floor."
    ),
    "instagramUrl": "https://instagram.com/steelflexmedchal",
    "whatsappNumber": "+91 90000 00000",
    "mapsUrl": "https://www.google.com/maps/search/steel+flex+gym+medchal",
    "footerText": "© 2026 Steel Flex, Medchal. Engineered Excellence.",
}


# ---------------------------------------------------------------------------
# Fetching
# ---------------------------------------------------------------------------

def fetch(url: str, name: str, kind: str) -> Path | None:
    """Download to the cache, verify the magic bytes, return the path or None."""
    CACHE.mkdir(exist_ok=True)
    path = CACHE / f"{name}.{kind}"

    if path.exists() and path.stat().st_size > 10_000:
        return path

    try:
        req = urllib.request.Request(url, headers={
            "User-Agent": UA,
            # Pexels' CDN 403s without it.
            "Referer": "https://www.pexels.com/",
        })
        data = urllib.request.urlopen(req, timeout=120).read()
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        print(f"   !  skipped {name}: {exc}")
        return None

    offset, magic = SIGNATURES[kind]
    if data[offset:offset + len(magic)] != magic:
        # Almost always a 404 HTML page wearing a .jpg name.
        print(f"   !  skipped {name}: not a valid {kind} ({len(data)} bytes)")
        return None

    path.write_bytes(data)
    return path


def local(path: Path) -> Path | None:
    if path.exists():
        return path
    print(f"   !  missing {path.name}")
    return None


# ---------------------------------------------------------------------------
# API
# ---------------------------------------------------------------------------

class Api:
    def __init__(self):
        self.s = requests.Session()
        self.s.verify = False
        try:
            r = self.s.post(f"{BASE_URL}/api/auth/login",
                            json={"email": OWNER_EMAIL, "password": OWNER_PASSWORD}, timeout=15)
        except requests.RequestException:
            sys.exit(f"API not reachable at {BASE_URL} — start it with `cd gymapis && dotnet run`.")
        if r.status_code != 200:
            sys.exit(f"Login failed ({r.status_code}). Check GYMAPI_OWNER_EMAIL / _PASSWORD.")
        self.s.headers["Authorization"] = f"Bearer {r.json()['accessToken']}"

    def get(self, path, **kw):
        return self.s.get(f"{BASE_URL}{path}", timeout=30, **kw)

    def post(self, path, **kw):
        return self.s.post(f"{BASE_URL}{path}", timeout=180, **kw)

    def put(self, path, **kw):
        return self.s.put(f"{BASE_URL}{path}", timeout=180, **kw)

    def delete(self, path, **kw):
        return self.s.delete(f"{BASE_URL}{path}", timeout=30, **kw)

    def ok(self, r, what: str) -> bool:
        if r.status_code in (200, 201, 204):
            return True
        print(f"   !  {what} failed [{r.status_code}]: {r.text[:200]}")
        return False


# ---------------------------------------------------------------------------
# Clear
# ---------------------------------------------------------------------------

def clear(api: Api) -> None:
    """Remove all website content so a re-run never duplicates."""
    print("Clearing existing website content")

    for kind in ("media", "offers", "transformations", "events"):
        removed = 0
        while True:
            r = api.get(f"/api/site/{kind}", params={"pageSize": 100})
            if r.status_code != 200:
                break
            items = r.json()["items"]
            if not items:
                break
            for item in items:
                if api.ok(api.delete(f"/api/site/{kind}/{item['id']}"), f"delete {kind}"):
                    removed += 1
            # A page that deletes nothing would spin forever.
            if removed == 0:
                break
        print(f"   removed {removed:>2} {kind}")

    # Artwork slots are cleared by posting remove=true rather than by deleting a
    # row — the settings singleton always exists.
    for slot in ("hero", "logo", "login"):
        api.post(f"/api/site/settings/artwork/{slot}", data={"remove": "true"})
    api.post("/api/site/settings/artwork/hero-video", data={"remove": "true"})
    print("   cleared artwork slots")

    # Plans are archived rather than deleted — a `memberships` row points at the
    # plan a member bought, so removing one would orphan their history.
    #
    # Every active plan is archived, not just the demo ones. On a demo database
    # that is the point: the pricing page should show the four plans below and
    # nothing left over from someone's testing. On a real database this script
    # has no business running at all — see the banner at the top of the file.
    r = api.get("/api/membership-plans", params={"pageSize": 100})
    if r.status_code == 200:
        archived = 0
        for plan in r.json()["items"]:
            if api.ok(api.delete(f"/api/membership-plans/{plan['id']}"), "archive plan"):
                archived += 1
        print(f"   archived {archived} plans")


# ---------------------------------------------------------------------------
# Seed
# ---------------------------------------------------------------------------

def seed(api: Api) -> None:
    counts = {}

    # -- settings ------------------------------------------------------------
    print("\nSettings")
    api.ok(api.put("/api/site/settings", json=SETTINGS), "settings")
    print("   hero copy, about copy, socials, footer")

    # -- artwork -------------------------------------------------------------
    print("\nArtwork")
    hero = fetch(HERO_IMAGE, "hero", "jpg")
    if hero:
        with hero.open("rb") as f:
            api.ok(api.post("/api/site/settings/artwork/hero", files={"file": f}), "hero image")
        print("   hero image")

    login = fetch(LOGIN_IMAGE, "login", "jpg")
    if login:
        with login.open("rb") as f:
            api.ok(api.post("/api/site/settings/artwork/login", files={"file": f}), "login image")
        print("   login image")

    hero_video = fetch(VIDEOS["hero"], "hero-video", "mp4")
    if hero_video:
        with hero_video.open("rb") as f:
            api.ok(api.post("/api/site/settings/artwork/hero-video", files={"file": f}),
                   "hero video")
        print(f"   hero video ({hero_video.stat().st_size // 1024 // 1024} MB)")

    # -- about collage: the real gym ----------------------------------------
    print("\nAbout collage (real Steel Flex photos)")
    n = 0
    for i, src in enumerate(REAL_GYM[:3]):
        path = local(src)
        if not path:
            continue
        with path.open("rb") as f:
            r = api.post("/api/site/media",
                         data={"Section": "about", "DisplayOrder": i, "IsActive": "true"},
                         files={"file": (f"about-{i}.webp", f, "image/webp")})
        if api.ok(r, "about media"):
            n += 1
    counts["about"] = n
    print(f"   {n} photos")

    # -- gallery -------------------------------------------------------------
    print("\nGallery")
    n = 0
    order = 0

    # The fourth real photo leads the gallery too — the client should see their
    # own floor before the stock starts.
    fourth = local(REAL_GYM[3])
    if fourth:
        with fourth.open("rb") as f:
            r = api.post("/api/site/media",
                         data={"Section": "gallery", "Caption": "Inside Steel Flex, Medchal",
                               "DisplayOrder": order, "IsActive": "true"},
                         files={"file": ("gallery-real.webp", f, "image/webp")})
        if api.ok(r, "gallery media"):
            n += 1
            order += 1

    for photo_id, caption in GALLERY_PHOTOS:
        path = fetch(unsplash(photo_id), f"gallery-{photo_id}", "jpg")
        if not path:
            continue
        with path.open("rb") as f:
            r = api.post("/api/site/media",
                         data={"Section": "gallery", "Caption": caption,
                               "DisplayOrder": order, "IsActive": "true"},
                         files={"file": (f"{photo_id}.jpg", f, "image/jpeg")})
        if api.ok(r, "gallery media"):
            n += 1
            order += 1

    # Each video ships with a poster. Without one the tile is a black rectangle
    # until someone presses play — `preload="none"` on the front end means the
    # browser fetches no frames at all, which is the right call for bandwidth
    # and the wrong look for a gallery. The API takes the poster on the same
    # request.
    # The poster ids are deliberately NOT in GALLERY_PHOTOS. Reusing one puts
    # the same photograph twice in the same mosaic, which reads as a bug.
    for key, caption, poster_id in (
        ("gallery1", "A session on the floor", "photo-1596357395217-80de13130e92"),
        ("gallery2", "Conditioning work", "photo-1591117207239-788bf8de6c3b"),
    ):
        path = fetch(VIDEOS[key], key, "mp4")
        if not path:
            continue

        poster = fetch(unsplash(poster_id, 1200), f"poster-{poster_id}", "jpg")
        files = {"file": (f"{key}.mp4", path.open("rb"), "video/mp4")}
        if poster:
            files["poster"] = (f"{key}-poster.jpg", poster.open("rb"), "image/jpeg")

        try:
            r = api.post("/api/site/media",
                         data={"Section": "gallery", "Caption": caption,
                               "DisplayOrder": order, "IsActive": "true"},
                         files=files)
        finally:
            for _, handle, _ in files.values():
                handle.close()

        if api.ok(r, "gallery video"):
            n += 1
            order += 1

    counts["gallery"] = n
    print(f"   {n} items")

    # -- plans ---------------------------------------------------------------
    print("\nMembership plans")
    catalogue = {}
    r = api.get("/api/lookups/plan-services")
    if r.status_code == 200:
        catalogue = {s["name"]: s["id"] for s in r.json()}

    n = 0
    for plan in PLANS:
        body = {k: v for k, v in plan.items() if k != "services"}
        # An unknown service id is a 400, so only send the ones this tenant has.
        body["serviceIds"] = [catalogue[s] for s in plan["services"] if s in catalogue]
        body["isActive"] = True
        # Unique per run: plan codes are unique per tenant and archived plans
        # keep theirs, so a re-run would otherwise collide.
        body["planCode"] = f"PLN-{uuid.uuid4().hex[:6].upper()}"
        if api.ok(api.post("/api/membership-plans", json=body), f"plan {plan['name']}"):
            n += 1
    counts["plans"] = n
    print(f"   {n} plans (Premium featured)")

    # -- offers --------------------------------------------------------------
    print("\nOffers")
    n = 0
    for i, offer in enumerate(OFFERS):
        if api.ok(api.post("/api/site/offers",
                           json={**offer, "displayOrder": i, "isActive": True}), "offer"):
            n += 1
    counts["offers"] = n
    print(f"   {n} offers")

    # -- transformations -----------------------------------------------------
    print("\nTransformations  (fabricated — see the banner at the top of this file)")
    n = 0
    for i, t in enumerate(TRANSFORMATIONS):
        before = fetch(unsplash(t["before"], 900), f"before-{t['before']}", "jpg")
        after = fetch(unsplash(t["after"], 900), f"after-{t['after']}", "jpg")
        if not before or not after:
            print(f"   !  skipped {t['name']}: missing a photo")
            continue

        with before.open("rb") as bf, after.open("rb") as af:
            r = api.post("/api/site/transformations", data={
                "DisplayName": t["name"],
                "Goal": t["goal"],
                "Achievement": t["achievement"],
                "DurationLabel": t["duration"],
                "Description": t["description"],
                "ConsentGiven": "true",
                "IsActive": "true",
                "DisplayOrder": i,
            }, files={"before": ("before.jpg", bf, "image/jpeg"),
                      "after": ("after.jpg", af, "image/jpeg")})
        if api.ok(r, f"transformation {t['name']}"):
            n += 1
    counts["transformations"] = n
    print(f"   {n} stories")

    # -- events --------------------------------------------------------------
    print("\nEvents")
    today = date.today()
    n = 0
    for i, e in enumerate(EVENTS):
        # Relative to today, so the seed still produces upcoming events next
        # month. The public endpoint hides anything in the past.
        when = today + timedelta(days=e["days"])
        image = fetch(unsplash(e["image"], 1200), f"event-{e['image']}", "jpg")

        data = {
            "Title": e["title"],
            "EventDate": when.isoformat(),
            "StartTime": e["start"],
            "EndTime": e["end"],
            "Location": e["location"],
            "Description": e["description"],
            "IsActive": "true",
            "DisplayOrder": i,
        }
        files = {}
        handle = None
        if image:
            handle = image.open("rb")
            files["image"] = (f"{e['image']}.jpg", handle, "image/jpeg")
        try:
            if api.ok(api.post("/api/site/events", data=data, files=files or None), "event"):
                n += 1
        finally:
            if handle:
                handle.close()
    counts["events"] = n
    print(f"   {n} events")

    # -- summary -------------------------------------------------------------
    print("\n" + "=" * 62)
    print("Seeded:", ", ".join(f"{v} {k}" for k, v in counts.items()))
    r = api.get("/api/public")
    if r.status_code == 200:
        d = r.json()
        print(f"Public payload: hero='{d['hero']['headline']}' "
              f"members={d['hero']['activeMembers']} years={d['hero']['yearsOfExperience']}")
        print(f"                {len(d['gallery'])} gallery, {len(d['plans'])} plans, "
              f"{len(d['offers'])} offers, {len(d['transformations'])} stories, "
              f"{len(d['events'])} events")
    print("=" * 62)
    print("REMINDER: the transformations are fabricated and carry consent nobody")
    print("gave. Run with --clear before this database is used for anything real.")


def main() -> None:
    parser = argparse.ArgumentParser(description="Seed the public website with demo content.")
    parser.add_argument("--clear", action="store_true", help="remove demo content and stop")
    args = parser.parse_args()

    api = Api()
    clear(api)
    if args.clear:
        print("\nCleared. Nothing seeded.")
        return
    seed(api)


if __name__ == "__main__":
    main()
