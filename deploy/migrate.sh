#!/bin/sh
# Applies each migration file once, in name order, and records it in schema_migrations.
# Interim runner for the BPA stack. The shared runner (in server-infra) replaces it later.
set -eu

psql -v ON_ERROR_STOP=1 -q -c "CREATE TABLE IF NOT EXISTS schema_migrations (
  filename text PRIMARY KEY,
  applied_at timestamptz NOT NULL DEFAULT now())"

for file in "${MIGRATIONS_DIR:-/migrations}"/*.sql; do
  name=$(basename "$file")
  already=$(psql -v ON_ERROR_STOP=1 -tA -c "SELECT count(*) FROM schema_migrations WHERE filename = '$name'")
  if [ "$already" = "1" ]; then
    echo "skip  $name"
    continue
  fi
  echo "apply $name"
  psql -v ON_ERROR_STOP=1 -1 -q -f "$file"
  psql -v ON_ERROR_STOP=1 -q -c "INSERT INTO schema_migrations (filename) VALUES ('$name')"
done

psql -v ON_ERROR_STOP=1 -v pw="$BPA_APP_LOGIN_PASSWORD" -q -f "${LOGIN_SQL:-/create_login_role.sql}"
echo "migrations done"
