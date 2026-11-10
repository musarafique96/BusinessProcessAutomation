# FlowPro: Business Process Automation Platform

Multi-tenant SaaS for small and mid-sized businesses. Design documents are in `docs/`. Delivery follows `docs/phase-plan.md`.

## Layout

- `src/Bpa.Api`: ASP.NET Core host (health endpoint for now).
- `src/Bpa.Tenancy`: tenant-scoped database access. `TenantDb` sets the tenant for one transaction only.
- `db/migrations/`: ordered SQL migrations, run as the migration owner, never as `bpa_app`.
- `tests/Bpa.Tenancy.Tests`: xUnit tests for the tenancy helper.
- `tests/sql/rls_checks.sql`: row-level security checks. These run without .NET.

## Running the checks locally

Requires PostgreSQL 16 and the .NET 10 SDK.

```bash
createdb bpa_test
psql -v ON_ERROR_STOP=1 -d bpa_test -f db/migrations/0001_tenancy.sql
psql -v ON_ERROR_STOP=1 -d bpa_test -f tests/sql/rls_checks.sql
export BPA_TEST_ADMIN_CONN="Host=localhost;Database=bpa_test;Username=postgres;Password=..."
dotnet test Bpa.slnx
```

## Tenant isolation rules

- Every tenant-scoped table has `tenant_id`, row-level security enabled and forced, and a policy that reads `app.tenant_id`.
- The application connects as `bpa_app`, which has no `BYPASSRLS`.
- A missing or empty tenant matches no rows (fail closed).
- Code opens tenant work only through `TenantDb.BeginTenantTransactionAsync`.
