-- Invitation checks for migration 0004. Run after 0001 to 0004, as the migration owner:
--   psql -v ON_ERROR_STOP=1 -f tests/sql/invitations_checks.sql
-- Each check raises an exception on failure, so psql exits non-zero and CI fails.

\set ON_ERROR_STOP on

INSERT INTO tenants (id, name, slug) VALUES
  ('a4000000-0000-0000-0000-00000000000a', 'Invite A', 'invite-a'),
  ('b4000000-0000-0000-0000-00000000000b', 'Invite B', 'invite-b')
ON CONFLICT (id) DO NOTHING;

DELETE FROM invitations WHERE tenant_id IN ('a4000000-0000-0000-0000-00000000000a', 'b4000000-0000-0000-0000-00000000000b');
DELETE FROM user_roles WHERE tenant_id IN ('a4000000-0000-0000-0000-00000000000a', 'b4000000-0000-0000-0000-00000000000b');
DELETE FROM users WHERE tenant_id IN ('a4000000-0000-0000-0000-00000000000a', 'b4000000-0000-0000-0000-00000000000b');
DELETE FROM roles WHERE tenant_id IN ('a4000000-0000-0000-0000-00000000000a', 'b4000000-0000-0000-0000-00000000000b');

-- Setup: an admin and a role in each company.
INSERT INTO users (id, tenant_id, username, password_hash, full_name, status) VALUES
  ('a4000000-0000-0000-0000-0000000000a1', 'a4000000-0000-0000-0000-00000000000a', 'inv.admin', 'hash', 'Admin A', 'active'),
  ('b4000000-0000-0000-0000-0000000000b1', 'b4000000-0000-0000-0000-00000000000b', 'inv.admin', 'hash', 'Admin B', 'active');

INSERT INTO roles (id, tenant_id, key, name) VALUES
  ('a4000000-0000-0000-0000-0000000000c1', 'a4000000-0000-0000-0000-00000000000a', 'staff', 'Staff'),
  ('b4000000-0000-0000-0000-0000000000c1', 'b4000000-0000-0000-0000-00000000000b', 'staff', 'Staff');

-- Check 1: an invitation needs exactly one identity. Neither is rejected.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO invitations (id, tenant_id, full_name, role_id, token_hash, invited_by, expires_at) VALUES
      ('a4000000-0000-0000-0000-0000000000d1', 'a4000000-0000-0000-0000-00000000000a', 'No Identity',
       'a4000000-0000-0000-0000-0000000000c1', 'hash-i1', 'a4000000-0000-0000-0000-0000000000a1', now() + interval '1 day');
  EXCEPTION WHEN check_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'INVITATION CHECK 1 FAILED: invitation without email or username accepted';
  END IF;
END
$$;

-- Check 2: both identities together are rejected too.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO invitations (id, tenant_id, email, username, full_name, role_id, token_hash, invited_by, expires_at) VALUES
      ('a4000000-0000-0000-0000-0000000000d2', 'a4000000-0000-0000-0000-00000000000a', 'both@example.com', 'both.user',
       'Both', 'a4000000-0000-0000-0000-0000000000c1', 'hash-i2', 'a4000000-0000-0000-0000-0000000000a1', now() + interval '1 day');
  EXCEPTION WHEN check_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'INVITATION CHECK 2 FAILED: invitation with both email and username accepted';
  END IF;
END
$$;

-- Check 3: a role from another company can't be used.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO invitations (id, tenant_id, username, full_name, role_id, token_hash, invited_by, expires_at) VALUES
      ('a4000000-0000-0000-0000-0000000000d3', 'a4000000-0000-0000-0000-00000000000a', 'cross.role',
       'Cross', 'b4000000-0000-0000-0000-0000000000c1', 'hash-i3', 'a4000000-0000-0000-0000-0000000000a1', now() + interval '1 day');
  EXCEPTION WHEN foreign_key_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'INVITATION CHECK 3 FAILED: invitation used a role from another company';
  END IF;
END
$$;

-- Check 4: only one pending invitation per username in a company.
INSERT INTO invitations (id, tenant_id, username, full_name, role_id, token_hash, invited_by, expires_at) VALUES
  ('a4000000-0000-0000-0000-0000000000d4', 'a4000000-0000-0000-0000-00000000000a', 'pending.user',
   'Pending', 'a4000000-0000-0000-0000-0000000000c1', 'hash-i4', 'a4000000-0000-0000-0000-0000000000a1', now() + interval '1 day');

DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    INSERT INTO invitations (id, tenant_id, username, full_name, role_id, token_hash, invited_by, expires_at) VALUES
      ('a4000000-0000-0000-0000-0000000000d5', 'a4000000-0000-0000-0000-00000000000a', 'pending.user',
       'Pending again', 'a4000000-0000-0000-0000-0000000000c1', 'hash-i5', 'a4000000-0000-0000-0000-0000000000a1', now() + interval '1 day');
  EXCEPTION WHEN unique_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'INVITATION CHECK 4 FAILED: two pending invitations for the same username';
  END IF;
END
$$;

-- Check 5: an accepted invitation must name the account it created, and a pending one must not.
DO $$
DECLARE
  blocked boolean := false;
BEGIN
  BEGIN
    UPDATE invitations SET accepted_at = now() WHERE id = 'a4000000-0000-0000-0000-0000000000d4';
  EXCEPTION WHEN check_violation THEN
    blocked := true;
  END;
  IF NOT blocked THEN
    RAISE EXCEPTION 'INVITATION CHECK 5 FAILED: accepted invitation without an account';
  END IF;
END
$$;

-- Check 6: row-level security. As the application role, company A can't see company B's invitations.
INSERT INTO invitations (id, tenant_id, username, full_name, role_id, token_hash, invited_by, expires_at) VALUES
  ('b4000000-0000-0000-0000-0000000000d1', 'b4000000-0000-0000-0000-00000000000b', 'b.visible',
   'B User', 'b4000000-0000-0000-0000-0000000000c1', 'hash-b1', 'b4000000-0000-0000-0000-0000000000b1', now() + interval '1 day');

DO $$
DECLARE
  visible bigint;
BEGIN
  SET LOCAL ROLE bpa_app;
  PERFORM set_config('app.tenant_id', 'a4000000-0000-0000-0000-00000000000a', true);
  SELECT count(*) INTO visible FROM invitations WHERE tenant_id = 'b4000000-0000-0000-0000-00000000000b';
  IF visible <> 0 THEN
    RAISE EXCEPTION 'INVITATION CHECK 6 FAILED: company A can see company B invitations';
  END IF;
END
$$;
