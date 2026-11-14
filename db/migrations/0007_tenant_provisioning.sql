-- 0007: creating a company (tenant).
-- The tenants table is a platform table. bpa_app can only read it, so the API creates a company
-- through this one function. It runs with the owner's rights (SECURITY DEFINER) and only bpa_app may call it.
-- The API calls it only after checking the platform admin key. Anything else that needs to create a
-- company should go through the same check.

CREATE OR REPLACE FUNCTION create_tenant(p_id uuid, p_name text, p_slug text)
RETURNS void
LANGUAGE sql
SECURITY DEFINER
SET search_path = public
AS $$
  INSERT INTO tenants (id, name, slug) VALUES (p_id, p_name, p_slug);
$$;

REVOKE ALL ON FUNCTION create_tenant(uuid, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION create_tenant(uuid, text, text) TO bpa_app;
