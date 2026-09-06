-- ============================================================================
-- MIGRATION 002 — refresh tokens (JWT auth)
-- Supersedes the `user_sessions` table listed as deferred in schema_v1.sql
-- ============================================================================
-- MODEL
--   access token   : JWT, 10-15 min, NEVER stored. Claims: sub (user_id),
--                    tenant_id, branch_id, role, exp, jti.
--   refresh token  : 256 bits of CSPRNG random, 7-30 days, stored HASHED here.
--                    Not a JWT — it carries no claims, it's just a lookup key.
--
-- WHY HASHED: a stored refresh token is a live credential. If the DB leaks,
-- raw tokens are immediately usable. SHA-256 (not bcrypt) is correct here —
-- the input is already high-entropy random, so there is nothing to brute
-- force, and bcrypt would add ~100ms to every single refresh call.
--
-- ROTATION + REUSE DETECTION
--   Each refresh call issues a new token and sets revoked_at + replaced_by_id
--   on the old one. All tokens descended from one login share a family_id.
--   If a token that is ALREADY revoked is presented, it was copied — revoke
--   the whole family and force re-login. This is what turns a stolen refresh
--   token from "attacker has weeks of access" into "attacker gets locked out
--   the moment either party refreshes."
-- ============================================================================

CREATE TABLE refresh_tokens (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       bigint      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id         bigint      NOT NULL REFERENCES users(id) ON DELETE CASCADE,

    -- SHA-256 of the raw token, hex or base64. Never store the raw value.
    token_hash      text        NOT NULL UNIQUE,

    -- One family per login. Rotation keeps the family, changes the token.
    family_id       uuid        NOT NULL DEFAULT gen_random_uuid(),

    -- Rotation chain: which token replaced this one
    replaced_by_id  bigint      REFERENCES refresh_tokens(id) ON DELETE SET NULL,

    expires_at      timestamptz NOT NULL,
    revoked_at      timestamptz,
    revoked_reason  text        CHECK (revoked_reason IN
                        ('rotated','logout','logout_all','password_change',
                         'reuse_detected','admin_revoked','user_deactivated')),

    -- device context: powers a "active sessions" screen and helps support
    ip_address      inet,
    user_agent      text,
    last_used_at    timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now()
);

-- the hot path: look up a presented token
CREATE INDEX idx_refresh_lookup ON refresh_tokens(token_hash);
-- "log out all devices" + revoke-on-password-change
CREATE INDEX idx_refresh_user_active ON refresh_tokens(user_id)
    WHERE revoked_at IS NULL;
-- reuse detection: kill an entire family at once
CREATE INDEX idx_refresh_family ON refresh_tokens(family_id);
-- nightly cleanup job
CREATE INDEX idx_refresh_expiry ON refresh_tokens(expires_at);


-- ---------------------------------------------------------------------------
-- REFRESH FLOW (pseudocode — put this in the auth service, in ONE transaction)
-- ---------------------------------------------------------------------------
--   row = SELECT * FROM refresh_tokens WHERE token_hash = sha256(presented)
--
--   if row IS NULL                  -> 401, unknown token
--
--   if row.revoked_at IS NOT NULL   -> REUSE DETECTED
--         UPDATE refresh_tokens
--            SET revoked_at = now(), revoked_reason = 'reuse_detected'
--          WHERE family_id = row.family_id AND revoked_at IS NULL;
--         -> 401, force full re-login
--
--   if row.expires_at < now()       -> 401, expired
--   if user.is_active = false       -> 401, deactivated
--
--   -- happy path: rotate
--   new = INSERT INTO refresh_tokens (tenant_id, user_id, token_hash,
--                                     family_id, expires_at, ip_address, user_agent)
--         VALUES (..., row.family_id, ...)   -- SAME family
--   UPDATE refresh_tokens
--      SET revoked_at = now(), revoked_reason = 'rotated',
--          replaced_by_id = new.id, last_used_at = now()
--    WHERE id = row.id;
--   -> return new access JWT + new refresh token
-- ---------------------------------------------------------------------------


-- ---------------------------------------------------------------------------
-- REVOCATION HELPERS
-- ---------------------------------------------------------------------------
-- Log out every device for one user (password change, deactivation, firing)
CREATE OR REPLACE FUNCTION revoke_user_tokens(p_user_id bigint, p_reason text)
RETURNS integer AS $$
    UPDATE refresh_tokens
       SET revoked_at = now(), revoked_reason = p_reason
     WHERE user_id = p_user_id AND revoked_at IS NULL;
    SELECT COUNT(*)::integer FROM refresh_tokens
     WHERE user_id = p_user_id AND revoked_at >= now() - interval '1 second';
$$ LANGUAGE sql;

-- Nightly cleanup. Keep revoked rows ~30 days for incident forensics —
-- a reuse_detected row is the evidence that someone was attacked.
CREATE OR REPLACE FUNCTION purge_expired_refresh_tokens()
RETURNS integer AS $$
    WITH deleted AS (
        DELETE FROM refresh_tokens
         WHERE expires_at < now() - interval '30 days'
        RETURNING 1
    )
    SELECT COUNT(*)::integer FROM deleted;
$$ LANGUAGE sql;


-- ---------------------------------------------------------------------------
-- RLS (only takes effect once rls.sql is applied)
-- ---------------------------------------------------------------------------
-- Add 'refresh_tokens' to the tenant_scoped array in rls.sql, or run:
--   ALTER TABLE refresh_tokens ENABLE ROW LEVEL SECURITY;
--   ALTER TABLE refresh_tokens FORCE  ROW LEVEL SECURITY;
--   CREATE POLICY tenant_isolation ON refresh_tokens
--       USING (tenant_id = current_tenant_id())
--       WITH CHECK (tenant_id = current_tenant_id());
--
-- CAUTION: the login endpoint runs BEFORE you know the tenant, so it cannot
-- have app.tenant_id set. Resolve the tenant from the subdomain first, SET
-- LOCAL app.tenant_id, then query users. Do not exempt auth from RLS.
-- ---------------------------------------------------------------------------
