-- 0009: align roles and assignments with the data model (sections 2 and 2 notes).
-- Renames keep existing data. Existing assignments become company-wide and open-ended.
-- Scopes (branch, department, project, own, assigned) are stored now, but a scoped assignment grants
-- nothing in permission checks until the organization module can resolve it. Checks fail closed.

ALTER TABLE roles RENAME COLUMN key TO code;
ALTER TABLE roles ADD COLUMN IF NOT EXISTS is_system boolean NOT NULL DEFAULT false;

ALTER TABLE permissions RENAME COLUMN key TO code;
ALTER TABLE role_permissions RENAME COLUMN permission_key TO permission_code;

ALTER TABLE user_roles RENAME TO role_assignments;
ALTER TABLE role_assignments DROP CONSTRAINT IF EXISTS user_roles_pkey;
ALTER TABLE role_assignments
  ADD COLUMN IF NOT EXISTS id uuid NOT NULL DEFAULT gen_random_uuid(),
  ADD COLUMN IF NOT EXISTS scope_type text NOT NULL DEFAULT 'company',
  ADD COLUMN IF NOT EXISTS scope_id uuid,
  ADD COLUMN IF NOT EXISTS starts_at timestamptz NOT NULL DEFAULT now(),
  ADD COLUMN IF NOT EXISTS ends_at timestamptz;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'role_assignments_pkey') THEN
    ALTER TABLE role_assignments ADD PRIMARY KEY (id);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'role_assignments_scope_check') THEN
    ALTER TABLE role_assignments ADD CONSTRAINT role_assignments_scope_check
      CHECK (scope_type IN ('company', 'branch', 'department', 'project', 'own', 'assigned'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'role_assignments_company_scope') THEN
    ALTER TABLE role_assignments ADD CONSTRAINT role_assignments_company_scope
      CHECK (scope_type <> 'company' OR scope_id IS NULL);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'role_assignments_end_after_start') THEN
    ALTER TABLE role_assignments ADD CONSTRAINT role_assignments_end_after_start
      CHECK (ends_at IS NULL OR ends_at > starts_at);
  END IF;
END
$$;

-- One open assignment per person, role and scope. Ended assignments stay as history.
CREATE UNIQUE INDEX IF NOT EXISTS role_assignments_open_key
  ON role_assignments (tenant_id, user_id, role_id, scope_type, COALESCE(scope_id, '00000000-0000-0000-0000-000000000000'::uuid))
  WHERE ends_at IS NULL;
