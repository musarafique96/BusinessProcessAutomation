-- Security stamp checks for migration 0006. Run after 0001 to 0006, as the migration owner:
--   psql -v ON_ERROR_STOP=1 -f tests/sql/security_stamp_checks.sql
-- Each check raises an exception on failure, so psql exits non-zero and CI fails.

\set ON_ERROR_STOP on

-- Check 1: every account has a security stamp, and it can't be left empty.
DO $$
DECLARE
  nullable text;
BEGIN
  SELECT is_nullable INTO nullable
  FROM information_schema.columns
  WHERE table_name = 'users' AND column_name = 'security_stamp';

  IF nullable IS DISTINCT FROM 'NO' THEN
    RAISE EXCEPTION 'SECURITY STAMP CHECK 1 FAILED: users.security_stamp is missing or nullable';
  END IF;
END
$$;
