-- ============================================================================
-- MIGRATION 003 — plan services (the "Included Services" catalog)
-- ============================================================================
-- The Add Membership Plan screen offers a grid of services to tick: Gym Floor
-- Access, Cardio Training, Personal Training, ... Those live here rather than
-- in `membership_plans.features text[]` because:
--
--   * renaming "Diet / Nutrition Consultation" is one UPDATE, not a rewrite of
--     every plan row that happens to contain that string;
--   * "which plans include Personal Training?" is a join, not an array scan;
--   * a controlled vocabulary stops typo'd near-duplicates the moment a second
--     person starts creating plans.
--
-- Same shape as the tags / staff_tags pair in schema_v1.sql — catalog table
-- scoped per tenant, composite-PK join, no surrogate id on the join.
--
-- `membership_plans.features` is deliberately left in place and unused. It is
-- reserved for free-text marketing bullets ("1 PT Session per month"), which
-- are prose about the plan rather than a selection from this catalog.
-- ============================================================================

CREATE TABLE plan_services (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name            text        NOT NULL,           -- 'Personal Training'
    description     text,
    is_active       boolean     NOT NULL DEFAULT true,  -- retire without deleting
    display_order   smallint    NOT NULL DEFAULT 0,     -- the order the grid renders
    UNIQUE (tenant_id, name)
);

-- Which services a given plan includes. Current state, not history: editing a
-- plan replaces the set. A plan's *past* contents are not something V1 needs,
-- and `memberships` already pins what a member actually bought.
CREATE TABLE membership_plan_services (
    plan_id         bigint NOT NULL REFERENCES membership_plans(id) ON DELETE CASCADE,
    service_id      bigint NOT NULL REFERENCES plan_services(id)   ON DELETE CASCADE,
    PRIMARY KEY (plan_id, service_id)
);
CREATE INDEX idx_plan_services_service ON membership_plan_services(service_id);


-- ---------------------------------------------------------------------------
-- Seed: the nine services the design ships with, in the order it draws them.
-- ON CONFLICT DO NOTHING so re-running this file is safe.
-- ---------------------------------------------------------------------------
INSERT INTO plan_services (tenant_id, name, display_order)
VALUES
    (1, 'Gym Floor Access',              1),
    (1, 'Strength Training',             2),
    (1, 'Cardio Training',               3),
    (1, 'Functional Training',           4),
    (1, 'Group Fitness Classes',         5),
    (1, 'Personal Training',             6),
    (1, 'Diet / Nutrition Consultation', 7),
    (1, 'Locker Facility',               8),
    (1, 'Shower Facility',               9)
ON CONFLICT (tenant_id, name) DO NOTHING;
