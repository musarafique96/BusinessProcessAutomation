-- 0008: audit trail foundation (data model section 10).
-- Append-only: the application role can insert and read audit events, but not change or delete them.
-- Each row is also tenant-scoped with row-level security, so one company can't read another's history.

CREATE TABLE IF NOT EXISTS audit_events (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants (id),
  occurred_at  timestamptz NOT NULL DEFAULT now(),
  actor_type   text NOT NULL CHECK (actor_type IN ('user', 'system', 'platform_support')),
  actor_id     uuid,
  action       text NOT NULL,
  target_type  text NOT NULL,
  target_id    text,
  before       jsonb,
  after        jsonb,
  reason       text,
  request_id   text,
  ip_address   text,
  session_id   text
);

CREATE INDEX IF NOT EXISTS audit_events_tenant_time ON audit_events (tenant_id, occurred_at);
CREATE INDEX IF NOT EXISTS audit_events_target ON audit_events (tenant_id, target_type, target_id);

ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_events FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON audit_events;
CREATE POLICY tenant_isolation ON audit_events
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

GRANT SELECT, INSERT ON audit_events TO bpa_app;
GRANT USAGE ON SEQUENCE audit_events_id_seq TO bpa_app;
