-- Password reset checks for migration 0005. Run after 0001 to 0005, as the migration owner:
--   psql -v ON_ERROR_STOP=1 -f tests/sql/password_resets_checks.sql
-- Each check raises an exception on failure, so psql exits non-zero and CI fails.

\set ON_ERROR_STOP on

INSERT INTO tenants (id, name, slug) VALUES
  ('a5000000-0000-0000-0000-00000000000a', 'Reset A', 'reset-a'),
  ('b5000000-0000-0000-0000-00000000000b', 'Reset B', 'reset-b')
ON CONFLICT (id) DO NOTHING;

DELETE FROM password_resets WHERE tenant_id IN ('a5000000-0000-0000-0000-00000000000a', 'b5000000-0000-0000-0000-00000000000b');
DELETE FROM users WHERE tenant_id IN ('a5000000-0000-0000-0000-00000000000a', 'b5000000-0000-0000-0000-00000000000b');

INSERT INTO users (id, tenant_id, username, password_hash, full_name, status) VALUES
  ('a5000000-0000-0000-0000-0000000000a1', 'a5000000-0000-0000-0000-00000000000a', 'reset.admin', 'hash', 'Admin A', 'active'),
  ('a5000000-0000-0000-0000-0000000000a2', 'a5000000-0000-0000-0000-00000000000a', 'reset.staff', 'hash', 'Staff A', 'active'),
  ('b5000000-0000-0000-0000-0000000000b1', 'b5000000-0000-0000-0000-00000000000b', 'reset.admin', 'hash', 'Admin B', 'active');

-- Check 1: only one open reset per account.
INSERT INTO password_resets (id, tenant_id, user_id, requested_by, token_hash, expires_at) VALUES
  ('a5000000-0000-0000-0000-0000000000c1', 'a5000000-0000-0000-0000-00000000000a', 'a5000000-0000-0000-0000-0000000000a2',
   'a5000000-0000-0000-0000-0000000000a1', 'hash-c1', now() + interval '1 hour');

DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO password_resets (id, tenant_id, user_id, requested_by, token_hash, expires_at) VALUES
      ('a5000000-0000-0000-0000-0000000000c2', 'a5000000-0000-0000-0000-00000000000a', 'a5000000-0000-0000-0000-0000000000a2',
       'a5000000-0000-0000-0000-0000000000a1', 'hash-c2', now() + interval '1 hour');
  EXCEPTION WHEN unique_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'PASSWORD RESET CHECK 1 FAILED: two open resets for one account';
  END IF;
END
$$;

-- Check 2: a reset can't point at an account in another company.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO password_resets (id, tenant_id, user_id, requested_by, token_hash, expires_at) VALUES
      ('a5000000-0000-0000-0000-0000000000c3', 'a5000000-0000-0000-0000-00000000000a', 'b5000000-0000-0000-0000-0000000000b1',
       'a5000000-0000-0000-0000-0000000000a1', 'hash-c3', now() + interval '1 hour');
  EXCEPTION WHEN foreign_key_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'PASSWORD RESET CHECK 2 FAILED: reset linked to an account in another company';
  END IF;
END
$$;

-- Check 3: row-level security. As the application role, company A can't see company B's resets.
INSERT INTO password_resets (id, tenant_id, user_id, requested_by, token_hash, expires_at) VALUES
  ('b5000000-0000-0000-0000-0000000000c1', 'b5000000-0000-0000-0000-00000000000b', 'b5000000-0000-0000-0000-0000000000b1',
   'b5000000-0000-0000-0000-0000000000b1', 'hash-b-c1', now() + interval '1 hour');

DO $$
DECLARE
  visible bigint;
BEGIN
  SET LOCAL ROLE bpa_app;
  PERFORM set_config('app.tenant_id', 'a5000000-0000-0000-0000-00000000000a', true);
  SELECT count(*) INTO visible FROM password_resets WHERE tenant_id = 'b5000000-0000-0000-0000-00000000000b';
  IF visible <> 0 THEN
    RAISE EXCEPTION 'PASSWORD RESET CHECK 3 FAILED: company A can see company B resets';
  END IF;
END
$$;
