-- 0001: tenant isolation foundation.
-- Run as the migration owner (never as bpa_app). The application connects as bpa_app,
-- which has no BYPASSRLS, so row-level security applies to every query it runs.

-- Application role. NOLOGIN here: the production login role is granted this role separately.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bpa_app') THEN
    CREATE ROLE bpa_app NOLOGIN NOBYPASSRLS;
  END IF;
END
$$;

-- Platform table: no tenant_id and no RLS (see docs/data-model.md, design rules).
CREATE TABLE IF NOT EXISTS tenants (
  id          uuid PRIMARY KEY,
  name        text NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now()
);

-- Tenant-scoped sample table used to prove isolation. Later modules follow the same pattern.
CREATE TABLE IF NOT EXISTS employees (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES tenants (id),
  full_name   text NOT NULL,
  email       text NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, email)
);

ALTER TABLE employees ENABLE ROW LEVEL SECURITY;
-- FORCE makes the policy apply even to the table owner.
ALTER TABLE employees FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_isolation ON employees;

-- NULLIF turns an empty setting into NULL, so a missing or empty tenant matches no rows (fail closed).
-- The same expression guards reads (USING) and writes (WITH CHECK).
CREATE POLICY tenant_isolation ON employees
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

GRANT USAGE ON SCHEMA public TO bpa_app;
GRANT SELECT ON tenants TO bpa_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON employees TO bpa_app;
