-- 0004: invitations. An admin invites a person by email or by username, with a role. The person
-- accepts by setting a password, and that creates the account. Only a hash of the token is stored.
-- Run as the migration owner.

CREATE TABLE IF NOT EXISTS invitations (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL REFERENCES tenants (id),
  email             text CHECK (email IS NULL OR email = lower(email)),
  username          text,
  full_name         text NOT NULL,
  role_id           uuid NOT NULL,
  token_hash        text NOT NULL UNIQUE,
  invited_by        uuid NOT NULL,
  expires_at        timestamptz NOT NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  accepted_at       timestamptz,
  accepted_user_id  uuid,
  revoked_at        timestamptz,
  CONSTRAINT invitations_one_identity CHECK ((username IS NULL) <> (email IS NULL)),
  CONSTRAINT invitations_username_format CHECK (username IS NULL OR username ~ '^[a-z0-9][a-z0-9._-]{2,31}$'),
  CONSTRAINT invitations_not_both_settled CHECK (accepted_at IS NULL OR revoked_at IS NULL),
  CONSTRAINT invitations_accepted_user_present CHECK ((accepted_at IS NULL) = (accepted_user_id IS NULL)),
  FOREIGN KEY (tenant_id, role_id) REFERENCES roles (tenant_id, id),
  FOREIGN KEY (tenant_id, invited_by) REFERENCES users (tenant_id, id),
  FOREIGN KEY (tenant_id, accepted_user_id) REFERENCES users (tenant_id, id)
);

-- One pending invitation per email and per username in a company.
CREATE UNIQUE INDEX IF NOT EXISTS invitations_pending_email_key
  ON invitations (tenant_id, email)
  WHERE email IS NOT NULL AND accepted_at IS NULL AND revoked_at IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS invitations_pending_username_key
  ON invitations (tenant_id, username)
  WHERE username IS NOT NULL AND accepted_at IS NULL AND revoked_at IS NULL;

ALTER TABLE invitations ENABLE ROW LEVEL SECURITY;
ALTER TABLE invitations FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON invitations;
CREATE POLICY tenant_isolation ON invitations
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

-- The application can create, accept and revoke invitations. It never deletes them.
GRANT SELECT, INSERT, UPDATE ON invitations TO bpa_app;
