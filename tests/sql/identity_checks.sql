-- Identity checks for migration 0002. Run after 0001 and 0002, as the migration owner:
--   psql -v ON_ERROR_STOP=1 -f tests/sql/identity_checks.sql
-- Each check raises an exception on failure, so psql exits non-zero and CI fails.

\set ON_ERROR_STOP on

-- Fixed tenants for these checks, cleaned up on each run.
INSERT INTO tenants (id, name, slug) VALUES
  ('c0000000-0000-0000-0000-00000000000c', 'Identity C', 'identity-c'),
  ('d0000000-0000-0000-0000-00000000000d', 'Identity D', 'identity-d')
ON CONFLICT (id) DO NOTHING;

DELETE FROM user_roles WHERE tenant_id IN ('c0000000-0000-0000-0000-00000000000c', 'd0000000-0000-0000-0000-00000000000d');
DELETE FROM role_permissions WHERE tenant_id IN ('c0000000-0000-0000-0000-00000000000c', 'd0000000-0000-0000-0000-00000000000d');
DELETE FROM roles WHERE tenant_id IN ('c0000000-0000-0000-0000-00000000000c', 'd0000000-0000-0000-0000-00000000000d');
DELETE FROM users WHERE tenant_id IN ('c0000000-0000-0000-0000-00000000000c', 'd0000000-0000-0000-0000-00000000000d');

-- Setup as owner: the same email exists in both tenants, which must be allowed.
INSERT INTO users (id, tenant_id, email, full_name, status) VALUES
  ('c1000000-0000-0000-0000-000000000001', 'c0000000-0000-0000-0000-00000000000c', 'shared@example.test', 'C User', 'active'),
  ('d1000000-0000-0000-0000-000000000001', 'd0000000-0000-0000-0000-00000000000d', 'shared@example.test', 'D User', 'active');

INSERT INTO roles (id, tenant_id, key, name) VALUES
  ('c2000000-0000-0000-0000-000000000001', 'c0000000-0000-0000-0000-00000000000c', 'admin', 'Admin'),
  ('d2000000-0000-0000-0000-000000000001', 'd0000000-0000-0000-0000-00000000000d', 'admin', 'Admin');

INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES
  ('c0000000-0000-0000-0000-00000000000c', 'c2000000-0000-0000-0000-000000000001', 'users.manage');

INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES
  ('c0000000-0000-0000-0000-00000000000c', 'c1000000-0000-0000-0000-000000000001', 'c2000000-0000-0000-0000-000000000001');

SET ROLE bpa_app;

-- Check 1: a tenant sees only its own users.
DO $$
DECLARE
  visible int;
BEGIN
  PERFORM set_config('app.tenant_id', 'c0000000-0000-0000-0000-00000000000c', true);
  SELECT count(*) INTO visible FROM users WHERE email = 'shared@example.test';
  IF visible <> 1 THEN
    RAISE EXCEPTION 'IDENTITY CHECK 1 FAILED: expected 1 user in tenant C, saw %', visible;
  END IF;
END
$$;

-- Check 2: a user can't be linked to a role in another tenant (composite foreign keys + RLS).
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  PERFORM set_config('app.tenant_id', 'c0000000-0000-0000-0000-00000000000c', true);
  BEGIN
    INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES
      ('c0000000-0000-0000-0000-00000000000c', 'c1000000-0000-0000-0000-000000000001', 'd2000000-0000-0000-0000-000000000001');
  EXCEPTION WHEN foreign_key_violation OR insufficient_privilege THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'IDENTITY CHECK 2 FAILED: tenant C linked a user to tenant D''s role';
  END IF;
END
$$;

-- Check 3: a duplicate email inside one tenant is rejected.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  PERFORM set_config('app.tenant_id', 'c0000000-0000-0000-0000-00000000000c', true);
  BEGIN
    INSERT INTO users (id, tenant_id, email, full_name, status) VALUES
      ('c1000000-0000-0000-0000-000000000099', 'c0000000-0000-0000-0000-00000000000c', 'shared@example.test', 'Dup', 'active');
  EXCEPTION WHEN unique_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'IDENTITY CHECK 3 FAILED: duplicate email accepted in one tenant';
  END IF;
END
$$;

-- Check 4: emails must be stored in lower case.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  PERFORM set_config('app.tenant_id', 'c0000000-0000-0000-0000-00000000000c', true);
  BEGIN
    INSERT INTO users (id, tenant_id, email, full_name, status) VALUES
      ('c1000000-0000-0000-0000-000000000098', 'c0000000-0000-0000-0000-00000000000c', 'Mixed@Example.test', 'Mixed', 'active');
  EXCEPTION WHEN check_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'IDENTITY CHECK 4 FAILED: mixed-case email accepted';
  END IF;
END
$$;

-- Check 5: permissions come only through an assigned role, and only within the user's tenant.
DO $$
DECLARE
  granted boolean;
  other_tenant_roles int;
BEGIN
  PERFORM set_config('app.tenant_id', 'c0000000-0000-0000-0000-00000000000c', true);
  SELECT EXISTS (
    SELECT 1
    FROM user_roles ur
    JOIN role_permissions rp ON rp.role_id = ur.role_id AND rp.tenant_id = ur.tenant_id
    JOIN users u ON u.id = ur.user_id AND u.tenant_id = ur.tenant_id
    WHERE ur.user_id = 'c1000000-0000-0000-0000-000000000001'
      AND rp.permission_key = 'users.manage'
      AND u.status = 'active'
  ) INTO granted;
  IF NOT granted THEN
    RAISE EXCEPTION 'IDENTITY CHECK 5 FAILED: assigned permission not found';
  END IF;

  PERFORM set_config('app.tenant_id', 'd0000000-0000-0000-0000-00000000000d', true);
  SELECT count(*) INTO other_tenant_roles FROM role_permissions WHERE tenant_id = 'c0000000-0000-0000-0000-00000000000c';
  IF other_tenant_roles <> 0 THEN
    RAISE EXCEPTION 'IDENTITY CHECK 5 FAILED: tenant D can read tenant C role permissions';
  END IF;
END
$$;

RESET ROLE;

\echo 'All identity checks passed.'
