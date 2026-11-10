-- Row-level security checks for tenant isolation (migration 0001).
-- Run after db/migrations/0001_tenancy.sql, as the migration owner:
--   psql -v ON_ERROR_STOP=1 -f tests/sql/rls_checks.sql
-- Every check raises an exception on failure, so psql exits non-zero and CI fails.
-- These checks run without .NET, so they also work as a quick local gate.

\set ON_ERROR_STOP on

-- Setup as the migration owner (bypasses RLS). Fixed ids keep reruns simple.
INSERT INTO tenants (id, name) VALUES
  ('11111111-1111-1111-1111-111111111111', 'Tenant A'),
  ('22222222-2222-2222-2222-222222222222', 'Tenant B')
ON CONFLICT (id) DO NOTHING;

DELETE FROM employees
WHERE tenant_id IN ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');

INSERT INTO employees (id, tenant_id, full_name, email) VALUES
  ('aaaaaaa1-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111', 'Ayesha', 'ayesha@a.test'),
  ('bbbbbbb1-0000-0000-0000-000000000001', '22222222-2222-2222-2222-222222222222', 'Bilal', 'bilal@b.test');

-- From here on, act as the application role, which has no BYPASSRLS.
SET ROLE bpa_app;

-- Check 1: a tenant sees its own rows and no other tenant's rows.
DO $$
DECLARE
  own_count int;
  other_count int;
BEGIN
  PERFORM set_config('app.tenant_id', '11111111-1111-1111-1111-111111111111', true);
  SELECT count(*) INTO own_count FROM employees WHERE full_name = 'Ayesha';
  SELECT count(*) INTO other_count FROM employees WHERE full_name = 'Bilal';
  IF own_count <> 1 THEN
    RAISE EXCEPTION 'CHECK 1 FAILED: tenant A should see its own row, saw %', own_count;
  END IF;
  IF other_count <> 0 THEN
    RAISE EXCEPTION 'CHECK 1 FAILED: tenant A can see tenant B row (count %)', other_count;
  END IF;
END
$$;

-- Check 2: with no tenant set, reads return nothing. Confirm rows exist so the zero is meaningful.
DO $$
DECLARE
  total_visible int;
  total_rows int;
BEGIN
  PERFORM set_config('app.tenant_id', '', true);
  SELECT count(*) INTO total_visible FROM employees;
  -- The setup inserted two rows as the owner. Visibility is checked through the owner below.
  IF total_visible <> 0 THEN
    RAISE EXCEPTION 'CHECK 2 FAILED: rows visible with no tenant set (count %)', total_visible;
  END IF;
  RESET ROLE;
  SELECT count(*) INTO total_rows FROM employees;
  SET ROLE bpa_app;
  IF total_rows < 2 THEN
    RAISE EXCEPTION 'CHECK 2 FAILED: setup rows missing, so the zero result proves nothing';
  END IF;
END
$$;

-- Check 3: a tenant cannot insert a row that belongs to another tenant.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  PERFORM set_config('app.tenant_id', '11111111-1111-1111-1111-111111111111', true);
  BEGIN
    INSERT INTO employees (id, tenant_id, full_name, email)
    VALUES ('ccccccc1-0000-0000-0000-000000000001', '22222222-2222-2222-2222-222222222222', 'Intruder', 'x@b.test');
  EXCEPTION WHEN insufficient_privilege THEN
    -- RLS WITH CHECK violations raise SQLSTATE 42501.
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'CHECK 3 FAILED: tenant A inserted a row for tenant B';
  END IF;
END
$$;

-- Check 4: a tenant cannot update or delete another tenant's row.
DO $$
DECLARE
  affected int;
  still_there int;
BEGIN
  PERFORM set_config('app.tenant_id', '11111111-1111-1111-1111-111111111111', true);
  UPDATE employees SET full_name = 'Hacked' WHERE full_name = 'Bilal';
  GET DIAGNOSTICS affected = ROW_COUNT;
  IF affected <> 0 THEN
    RAISE EXCEPTION 'CHECK 4 FAILED: tenant A updated % row(s) of tenant B', affected;
  END IF;
  DELETE FROM employees WHERE full_name = 'Bilal';
  GET DIAGNOSTICS affected = ROW_COUNT;
  IF affected <> 0 THEN
    RAISE EXCEPTION 'CHECK 4 FAILED: tenant A deleted % row(s) of tenant B', affected;
  END IF;

  PERFORM set_config('app.tenant_id', '22222222-2222-2222-2222-222222222222', true);
  SELECT count(*) INTO still_there FROM employees WHERE full_name = 'Bilal';
  IF still_there <> 1 THEN
    RAISE EXCEPTION 'CHECK 4 FAILED: tenant B row was changed or removed';
  END IF;
END
$$;

-- Check 5: the tenant setting does not survive the transaction (no leak into pooled connections).
BEGIN;
\o /dev/null
SELECT set_config('app.tenant_id', '11111111-1111-1111-1111-111111111111', true);
\o
COMMIT;

DO $$
DECLARE
  leaked text;
  visible int;
BEGIN
  leaked := current_setting('app.tenant_id', true);
  IF leaked IS NOT NULL AND leaked <> '' THEN
    RAISE EXCEPTION 'CHECK 5 FAILED: tenant setting leaked after commit: %', leaked;
  END IF;
  SELECT count(*) INTO visible FROM employees;
  IF visible <> 0 THEN
    RAISE EXCEPTION 'CHECK 5 FAILED: rows visible after the tenant transaction ended';
  END IF;
END
$$;

RESET ROLE;

\echo 'All RLS checks passed.'
