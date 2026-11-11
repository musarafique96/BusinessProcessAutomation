-- Username and Gmail checks for migration 0003. Run after 0001, 0002 and 0003, as the migration owner:
--   psql -v ON_ERROR_STOP=1 -f tests/sql/identity_username_checks.sql
-- Each check raises an exception on failure, so psql exits non-zero and CI fails.

\set ON_ERROR_STOP on

INSERT INTO tenants (id, name, slug) VALUES
  ('e0000000-0000-0000-0000-00000000000e', 'Username E', 'username-e'),
  ('f0000000-0000-0000-0000-00000000000f', 'Username F', 'username-f')
ON CONFLICT (id) DO NOTHING;

DELETE FROM users WHERE tenant_id IN ('e0000000-0000-0000-0000-00000000000e', 'f0000000-0000-0000-0000-00000000000f');

-- Setup: the same username exists in both tenants, which must be allowed.
INSERT INTO users (id, tenant_id, username, password_hash, full_name, status) VALUES
  ('e1000000-0000-0000-0000-000000000001', 'e0000000-0000-0000-0000-00000000000e', 'ayesha.k', 'hash', 'Ayesha', 'active'),
  ('f1000000-0000-0000-0000-000000000001', 'f0000000-0000-0000-0000-00000000000f', 'ayesha.k', 'hash', 'Ayesha F', 'active');

-- Check 1: a username is unique inside one company (the check above allowed the same name across companies).
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO users (id, tenant_id, username, password_hash, full_name, status) VALUES
      ('e1000000-0000-0000-0000-000000000002', 'e0000000-0000-0000-0000-00000000000e', 'ayesha.k', 'hash', 'Dup', 'active');
  EXCEPTION WHEN unique_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'USERNAME CHECK 1 FAILED: duplicate username accepted in one company';
  END IF;
END
$$;

-- Check 2: a username can't change once set.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    UPDATE users SET username = 'renamed' WHERE id = 'e1000000-0000-0000-0000-000000000001';
  EXCEPTION WHEN check_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'USERNAME CHECK 2 FAILED: username was changed';
  END IF;
END
$$;

-- Check 3: a username needs a password.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO users (id, tenant_id, username, full_name, status) VALUES
      ('e1000000-0000-0000-0000-000000000003', 'e0000000-0000-0000-0000-00000000000e', 'no.password', 'No Password', 'active');
  EXCEPTION WHEN check_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'USERNAME CHECK 3 FAILED: username accepted without a password';
  END IF;
END
$$;

-- Check 4: usernames are lower case only.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO users (id, tenant_id, username, password_hash, full_name, status) VALUES
      ('e1000000-0000-0000-0000-000000000004', 'e0000000-0000-0000-0000-00000000000e', 'Mixed.Case', 'hash', 'Mixed', 'active');
  EXCEPTION WHEN check_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'USERNAME CHECK 4 FAILED: mixed-case username accepted';
  END IF;
END
$$;

-- Check 5: an account needs a username or an email.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO users (id, tenant_id, full_name, status) VALUES
      ('e1000000-0000-0000-0000-000000000005', 'e0000000-0000-0000-0000-00000000000e', 'Nobody', 'active');
  EXCEPTION WHEN check_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'USERNAME CHECK 5 FAILED: account created with no username or email';
  END IF;
END
$$;

-- Check 6: a Gmail account can exist with no username and no password (Gmail-only sign-in).
INSERT INTO users (id, tenant_id, email, google_subject, full_name, status) VALUES
  ('e1000000-0000-0000-0000-000000000006', 'e0000000-0000-0000-0000-00000000000e', 'gmail.only@example.test', 'google-sub-e6', 'Gmail Only', 'active');

-- Check 7: one Gmail account links to only one user in a company.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO users (id, tenant_id, email, google_subject, full_name, status) VALUES
      ('e1000000-0000-0000-0000-000000000007', 'e0000000-0000-0000-0000-00000000000e', 'other@example.test', 'google-sub-e6', 'Dup Gmail', 'active');
  EXCEPTION WHEN unique_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'USERNAME CHECK 7 FAILED: one Gmail account linked to two users in a company';
  END IF;
END
$$;

-- Check 8: the linked Gmail can't change, and the email can't change while Gmail is linked.
DO $$
DECLARE
  blocked_subject boolean := false;
  blocked_email boolean := false;
BEGIN
  BEGIN
    UPDATE users SET google_subject = 'google-sub-other' WHERE id = 'e1000000-0000-0000-0000-000000000006';
  EXCEPTION WHEN check_violation THEN
    blocked_subject := true;
  END;
  BEGIN
    UPDATE users SET email = 'changed@example.test' WHERE id = 'e1000000-0000-0000-0000-000000000006';
  EXCEPTION WHEN check_violation THEN
    blocked_email := true;
  END;
  IF NOT blocked_subject THEN
    RAISE EXCEPTION 'USERNAME CHECK 8 FAILED: linked Gmail was changed';
  END IF;
  IF NOT blocked_email THEN
    RAISE EXCEPTION 'USERNAME CHECK 8 FAILED: email changed while Gmail is linked';
  END IF;
END
$$;

\echo 'All username and Gmail checks passed.'
