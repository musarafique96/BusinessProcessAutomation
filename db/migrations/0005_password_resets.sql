-- 0005: password resets. An admin with users.manage issues a one-time reset link for an account.
-- The person sets a new password with it. Only a hash of the token is stored.
-- Run as the migration owner.

CREATE TABLE IF NOT EXISTS password_resets (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES tenants (id),
  user_id       uuid NOT NULL,
  requested_by  uuid NOT NULL,
  token_hash    text NOT NULL UNIQUE,
  expires_at    timestamptz NOT NULL,
  created_at    timestamptz NOT NULL DEFAULT now(),
  used_at       timestamptz,
  FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id),
  FOREIGN KEY (tenant_id, requested_by) REFERENCES users (tenant_id, id)
);

-- At most one open reset per account. A new reset replaces the previous one.
CREATE UNIQUE INDEX IF NOT EXISTS password_resets_one_open_key
  ON password_resets (tenant_id, user_id)
  WHERE used_at IS NULL;

ALTER TABLE password_resets ENABLE ROW LEVEL SECURITY;
ALTER TABLE password_resets FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON password_resets;
CREATE POLICY tenant_isolation ON password_resets
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

GRANT SELECT, INSERT, UPDATE ON password_resets TO bpa_app;
