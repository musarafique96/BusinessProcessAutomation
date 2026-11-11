-- 0002: identity foundation. Tenants, users, roles and permissions, all tenant-scoped with RLS.
-- Run as the migration owner. The application role bpa_app can read and write these tables only
-- inside a tenant context (see Bpa.Tenancy.TenantDb).

-- Tenant settings (defaults from the assumptions register: A12 Asia/Karachi, PKR).
ALTER TABLE tenants
  ADD COLUMN IF NOT EXISTS slug text,
  ADD COLUMN IF NOT EXISTS timezone text NOT NULL DEFAULT 'Asia/Karachi',
  ADD COLUMN IF NOT EXISTS currency char(3) NOT NULL DEFAULT 'PKR',
  ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'active';

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'tenants_status_check') THEN
    ALTER TABLE tenants ADD CONSTRAINT tenants_status_check CHECK (status IN ('active', 'suspended'));
  END IF;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS tenants_slug_key ON tenants (slug);

-- Permission catalogue. Platform-wide, so no tenant_id and no RLS.
CREATE TABLE IF NOT EXISTS permissions (
  key          text PRIMARY KEY,
  description  text NOT NULL
);

INSERT INTO permissions (key, description) VALUES
  ('users.manage',     'Invite users and change their roles'),
  ('roles.manage',     'Create roles and set their permissions'),
  ('requests.create',  'Submit requests'),
  ('requests.read',    'View requests within scope')
ON CONFLICT (key) DO NOTHING;

CREATE TABLE IF NOT EXISTS users (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants (id),
  email          text NOT NULL CHECK (email = lower(email)),
  full_name      text NOT NULL,
  password_hash  text,
  status         text NOT NULL DEFAULT 'invited' CHECK (status IN ('invited', 'active', 'disabled')),
  created_at     timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, email),
  -- Lets other tables reference (tenant_id, user_id) so links can't cross tenants.
  UNIQUE (tenant_id, id)
);

CREATE TABLE IF NOT EXISTS roles (
  id         uuid PRIMARY KEY,
  tenant_id  uuid NOT NULL REFERENCES tenants (id),
  key        text NOT NULL,
  name       text NOT NULL,
  UNIQUE (tenant_id, key),
  UNIQUE (tenant_id, id)
);

CREATE TABLE IF NOT EXISTS role_permissions (
  tenant_id       uuid NOT NULL,
  role_id         uuid NOT NULL,
  permission_key  text NOT NULL REFERENCES permissions (key),
  PRIMARY KEY (role_id, permission_key),
  FOREIGN KEY (tenant_id, role_id) REFERENCES roles (tenant_id, id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS user_roles (
  tenant_id  uuid NOT NULL,
  user_id    uuid NOT NULL,
  role_id    uuid NOT NULL,
  PRIMARY KEY (user_id, role_id),
  FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, role_id) REFERENCES roles (tenant_id, id) ON DELETE CASCADE
);

-- Row-level security on every tenant-scoped table. Same fail-closed rule as migration 0001.
ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON users;
CREATE POLICY tenant_isolation ON users
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE roles ENABLE ROW LEVEL SECURITY;
ALTER TABLE roles FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON roles;
CREATE POLICY tenant_isolation ON roles
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE role_permissions ENABLE ROW LEVEL SECURITY;
ALTER TABLE role_permissions FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON role_permissions;
CREATE POLICY tenant_isolation ON role_permissions
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE user_roles ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_roles FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON user_roles;
CREATE POLICY tenant_isolation ON user_roles
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

GRANT SELECT ON permissions TO bpa_app;
GRANT SELECT, INSERT, UPDATE ON users TO bpa_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON roles, role_permissions, user_roles TO bpa_app;
