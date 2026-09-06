-- ============================================================================
-- MIGRATION 008 — the public website and the CMS behind it
-- ============================================================================
-- The gym gets a marketing site the owner edits from the admin panel. Five
-- public pages (Home, Plans, Transformations, Events, Contact) and five new
-- tables. `membership_plans` is reused as-is — the plans page reads the same
-- catalogue the desk sells from, so a price can never be right in one place
-- and wrong in the other.
--
-- WHAT IS DELIBERATELY *NOT* HERE
--
-- `site_settings` does not store the gym's name, tagline, logo, phone, email or
-- address. Those already exist: `tenants.name / tagline / logo_url` and
-- `branches.phone / email / address_line1 / city / ...`. Copying them into a
-- website table would create a second source of truth for the gym's own phone
-- number, and the two would drift the first week someone edited the wrong
-- screen. The public API reads through to those rows; this table holds only
-- fields that exist *because* there is a website — hero artwork, the About
-- copy, the social links, the footer line.
--
-- The same reasoning is why there is no `site_features` table for the design's
-- "Why Choose Our Gym" cards. That copy is not something the client has asked
-- to edit, and a CMS table nobody writes to is a migration plus a screen plus a
-- test for nothing. It ships as static page copy; when the owner asks to change
-- it, it becomes a table.
--
-- ONE TABLE FOR TWO GALLERIES
--
-- The requirements list "About photos" and "Gallery" separately, but they are
-- the same row: a URL, a caption, an order and a toggle. `site_media.section`
-- discriminates. Two identical tables would mean two entities, two services and
-- two CMS screens that drift apart.
--
-- ORDERING
--
-- `display_order smallint` everywhere, the convention `membership_plans` and
-- `plan_services` already use. Not unique, not gapless — the reorder endpoint
-- rewrites the whole run, and a gap is invisible to an ORDER BY.
--
-- ACTIVE IS NOT DELETED
--
-- `is_active` here is an editor's visibility toggle, and rows are hard-deleted
-- when the owner removes them. That is the one place this module departs from
-- "nothing is hard-deleted", and it is the right call: a gallery photo is
-- content, not a record of something that happened. There is no ledger to
-- protect and no audit question that a deleted marketing image answers. The
-- deletion is still written to `activity_log`.
-- ============================================================================


-- ---------------------------------------------------------------------------
-- site_settings — the singleton. One row per tenant, created by the seed below
-- so every read path can assume it exists and no screen has to handle "not
-- configured yet" as a distinct state.
-- ---------------------------------------------------------------------------
CREATE TABLE site_settings (
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id               bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,

    -- Hero. Headline and subtext are nullable because the fallback is real:
    -- tenants.name and tenants.tagline. A gym that never opens this screen
    -- still gets a correct hero.
    hero_headline           text,
    hero_subtext            text,
    hero_image_url          text,
    hero_video_url          text,

    -- The two hero stat tiles. `founded_year` is stored and the years of
    -- experience derived from it, so the number is never stale in January.
    -- `member_count_override` is NULL by default and the count comes live from
    -- v_member_overview — a real figure the owner cannot forget to update.
    -- The override exists because a brand-new gym may not want to publish "7".
    founded_year            smallint    CHECK (founded_year BETWEEN 1900 AND 2200),
    member_count_override   integer     CHECK (member_count_override >= 0),

    -- About section
    about_title             text,
    about_description       text,

    -- Brand artwork that is specific to the website / login screen. The gym's
    -- own logo lives on tenants.logo_url; these are the web-sized derivatives
    -- and the login page's left-hand image.
    website_logo_url        text,
    login_image_url         text,

    -- Contact extras. Phone/email/address come from `branches`; these three
    -- have no home anywhere else.
    instagram_url           text,
    whatsapp_number         text,
    maps_url                text,

    footer_text             text,

    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),

    UNIQUE (tenant_id)
);

CREATE TRIGGER trg_site_settings_updated BEFORE UPDATE ON site_settings
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();


-- ---------------------------------------------------------------------------
-- site_media — About photos and the Gallery. Images and videos.
--
-- `poster_url` is the still frame a <video> shows before play. Without one the
-- browser either shows black or downloads the first frame of every video on the
-- page, which on a phone connection is the whole gallery.
-- ---------------------------------------------------------------------------
CREATE TABLE site_media (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    section         text        NOT NULL CHECK (section IN ('about','gallery')),
    kind            text        NOT NULL CHECK (kind    IN ('image','video')),
    url             text        NOT NULL,
    poster_url      text,
    caption         text,
    display_order   smallint    NOT NULL DEFAULT 0,
    is_active       boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TRIGGER trg_site_media_updated BEFORE UPDATE ON site_media
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- The public read is always "one section, visible only, in order". Partial
-- index so the inactive rows the CMS still lists do not sit in it.
CREATE INDEX idx_site_media_public
    ON site_media (tenant_id, section, display_order)
    WHERE is_active;


-- ---------------------------------------------------------------------------
-- site_offers — the "This Month's Exclusive Offers" strip.
--
-- No start/end dates. The requirement is a toggle, and a date window is a
-- second mechanism that can disagree with it ("active, but expired"). When the
-- client asks for scheduling, add the columns and let the toggle mean "even if
-- scheduled, off".
-- ---------------------------------------------------------------------------
CREATE TABLE site_offers (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    title           text        NOT NULL,
    value_label     text,                           -- '10% OFF', 'FREE'
    description     text,
    display_order   smallint    NOT NULL DEFAULT 0,
    is_active       boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TRIGGER trg_site_offers_updated BEFORE UPDATE ON site_offers
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE INDEX idx_site_offers_public
    ON site_offers (tenant_id, display_order)
    WHERE is_active;


-- ---------------------------------------------------------------------------
-- site_transformations — before/after success stories.
--
-- DPDP: THIS TABLE PUBLISHES A REAL PERSON'S NAME AND BODY PHOTOGRAPHS TO THE
-- OPEN INTERNET. That is a new purpose, distinct from the membership contract
-- the member signed, and it needs its own consent.
--
--   * `consent_given_at` is NULL until a human ticks the box. The public
--     projection filters on `consent_given_at IS NOT NULL`, and the service
--     refuses to set `is_active` without it. Two gates, because one of them
--     will eventually be edited by someone who does not know why it is there.
--   * `consent_captured_by` records which staff account took the consent. It
--     comes off the validated JWT, never off the request body — the same rule
--     every audit row in this schema follows.
--   * `member_id` is nullable and ON DELETE SET NULL. Erasing a member under
--     `MemberService.EraseAsync` must not fail on a foreign key, and it must
--     not silently leave their photos on the website either: the erase path
--     deletes the transformation rows outright.
--   * `display_name` is stored rather than joined so a member can consent to
--     "Rahul S." instead of their full legal name.
-- ---------------------------------------------------------------------------
CREATE TABLE site_transformations (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id           bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    member_id           bigint      REFERENCES members(id) ON DELETE SET NULL,

    display_name        text        NOT NULL,
    goal                text,                       -- 'Muscle Gain'
    before_image_url    text        NOT NULL,
    after_image_url     text        NOT NULL,
    achievement         text,                       -- 'Gained 8 kg Muscle'
    duration_label      text,                       -- '6 Months'
    description         text,

    consent_given_at    timestamptz,
    consent_captured_by bigint      REFERENCES users(id) ON DELETE SET NULL,

    display_order       smallint    NOT NULL DEFAULT 0,
    is_active           boolean     NOT NULL DEFAULT false,   -- off until consented
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),

    -- The invariant, stated where it cannot be forgotten: a published
    -- transformation has recorded consent. The application checks this too and
    -- returns a 400; this constraint is what makes a bug a 500 instead of a
    -- privacy incident.
    CONSTRAINT site_transformations_consent_required
        CHECK (is_active = false OR consent_given_at IS NOT NULL)
);

CREATE TRIGGER trg_site_transformations_updated BEFORE UPDATE ON site_transformations
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE INDEX idx_site_transformations_public
    ON site_transformations (tenant_id, display_order)
    WHERE is_active AND consent_given_at IS NOT NULL;

CREATE INDEX idx_site_transformations_member
    ON site_transformations (member_id)
    WHERE member_id IS NOT NULL;


-- ---------------------------------------------------------------------------
-- site_events — upcoming classes, challenges, workshops.
--
-- `event_date` is a date and `start_time`/`end_time` are times, not one
-- timestamptz: an event is "6:00 AM at the Downtown studio", a wall-clock fact
-- about the branch's calendar, and storing an instant would make it drift with
-- the reader's timezone. Same reasoning as IBranchClock in attendance.
--
-- `end_time` is nullable — a daily challenge has a start and no end.
-- ---------------------------------------------------------------------------
CREATE TABLE site_events (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    title           text        NOT NULL,
    image_url       text,
    event_date      date        NOT NULL,
    start_time      time,
    end_time        time,
    location        text,
    description     text,
    display_order   smallint    NOT NULL DEFAULT 0,
    is_active       boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TRIGGER trg_site_events_updated BEFORE UPDATE ON site_events
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- The public list is "upcoming, soonest first", so the index leads with the
-- date rather than display_order.
CREATE INDEX idx_site_events_public
    ON site_events (tenant_id, event_date)
    WHERE is_active;


-- ---------------------------------------------------------------------------
-- membership_plans gains the "MOST POPULAR" badge.
--
-- Not derived from price or position: which plan the gym wants to push is a
-- marketing decision, and deriving it (the middle one? the dearest?) would mean
-- reordering the list silently moved the badge.
-- ---------------------------------------------------------------------------
ALTER TABLE membership_plans
    ADD COLUMN IF NOT EXISTS is_featured boolean NOT NULL DEFAULT false;


-- ---------------------------------------------------------------------------
-- Seed the singleton. ON CONFLICT DO NOTHING so re-running this file is safe
-- and so an existing configured site is never blanked by a second apply.
-- ---------------------------------------------------------------------------
INSERT INTO site_settings (tenant_id)
VALUES (1)
ON CONFLICT (tenant_id) DO NOTHING;
