-- The login role the API connects as. It inherits bpa_app's grants and has no BYPASSRLS,
-- so row-level security applies. The password comes from BPA_APP_LOGIN_PASSWORD via psql variable pw.
SELECT 'CREATE ROLE bpa_login' WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bpa_login') \gexec
SELECT format('ALTER ROLE bpa_login WITH LOGIN NOBYPASSRLS PASSWORD %L', :'pw') \gexec
GRANT bpa_app TO bpa_login;
