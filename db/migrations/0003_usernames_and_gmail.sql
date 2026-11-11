-- 0003: usernames and Gmail sign-in on users. Same sign-in rules as Gharbaar:
-- username and password, and Gmail. Phone sign-in comes later (paid provider).
-- Run as the migration owner.

-- Email becomes optional, because a username-only account has no email.
ALTER TABLE users ALTER COLUMN email DROP NOT NULL;

ALTER TABLE users
  ADD COLUMN IF NOT EXISTS username text,
  ADD COLUMN IF NOT EXISTS google_subject text;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'users_username_format') THEN
    -- Lower case, 3 to 32 characters: letters, digits, dot, underscore, hyphen.
    ALTER TABLE users ADD CONSTRAINT users_username_format
      CHECK (username IS NULL OR username ~ '^[a-z0-9][a-z0-9._-]{2,31}$');
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'users_identity_present') THEN
    ALTER TABLE users ADD CONSTRAINT users_identity_present
      CHECK (username IS NOT NULL OR email IS NOT NULL);
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'users_username_needs_password') THEN
    -- A username is a password sign-in, so the account needs a password.
    ALTER TABLE users ADD CONSTRAINT users_username_needs_password
      CHECK (username IS NULL OR password_hash IS NOT NULL);
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'users_google_needs_email') THEN
    ALTER TABLE users ADD CONSTRAINT users_google_needs_email
      CHECK (google_subject IS NULL OR email IS NOT NULL);
  END IF;
END
$$;

-- A username is unique within a company. A Gmail account is unique within a company too.
CREATE UNIQUE INDEX IF NOT EXISTS users_tenant_username_key ON users (tenant_id, username);
CREATE UNIQUE INDEX IF NOT EXISTS users_tenant_google_key ON users (tenant_id, google_subject);

-- Once set, the username and the linked Gmail can't change. While Gmail is linked, the email can't change.
-- A trigger is used because these rules must hold even for the owner role and for any future code path.
CREATE OR REPLACE FUNCTION users_guard_identity() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.username IS NOT NULL AND NEW.username IS DISTINCT FROM OLD.username THEN
    RAISE EXCEPTION 'username cannot be changed once set' USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.google_subject IS NOT NULL AND NEW.google_subject IS DISTINCT FROM OLD.google_subject THEN
    RAISE EXCEPTION 'linked Gmail cannot be changed' USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.google_subject IS NOT NULL AND NEW.email IS DISTINCT FROM OLD.email THEN
    RAISE EXCEPTION 'email cannot be changed while Gmail is linked' USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS users_guard_identity ON users;
CREATE TRIGGER users_guard_identity
  BEFORE UPDATE ON users
  FOR EACH ROW EXECUTE FUNCTION users_guard_identity();
