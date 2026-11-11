# BPA Deployment Plan

Status: plan agreed for development. Infrastructure is not set up yet. Operational readiness is Phase 7 (see `phase-plan.md`).

## Stage 1: development (now)

One Oracle Cloud Always Free Ampere A1 virtual machine, called the shared server. It runs both the BPA and Gharbaar projects.

```
Shared server (Oracle, Always Free Ampere A1)
├── Edge: Caddy (ports 80 and 443)
│   ├── gharbaar.duckdns.org → Gharbaar API
│   └── bpa.duckdns.org      → BPA API
├── Gharbaar
│   └── Gharbaar API → Gharbaar PostgreSQL database (name: gharbaar)
└── BPA
    └── BPA API → BPA PostgreSQL database (name: bpa)
```

Gharbaar's notes plan to switch from DuckDNS and Caddy to a Cloudflare Tunnel later. That change affects only the edge. Both projects keep their own API and database.

**Repositories.** Both projects live under the same GitHub account in separate repositories. BPA is `musarafique96/BusinessProcessAutomation`.

**Same approach for both projects.** Each project has its own database, but they're run and backed up the same way.

- **Containers:** each project runs under its own Docker Compose file in its own directory. Each has its own PostgreSQL 16 container and its own data volume, so `docker compose down -v` in one project never touches the other. The BPA application also runs in a container.
- **Isolation:** separate databases (`bpa` and `gharbaar`), each with its own login role. Neither role has rights on the other's database. The BPA application connects as `bpa_app`, which has no `BYPASSRLS`, so row-level security applies in production as it does in tests.
- **Network:** PostgreSQL listens only on the internal Docker network. Only the application container can reach it. Only ports 80, 443 and SSH are open to the internet.
- **Routing and TLS:** Caddy terminates TLS and routes each DuckDNS subdomain to its application, matching the Gharbaar setup.
- **Secrets:** each project keeps its secrets in a file on the server, outside the repository.
- **Schema changes:** BPA gets a migration runner that applies migration files in order and records which ones have run. Gharbaar uses the same runner once its schema changes. Container init scripts run only on a fresh volume, so they aren't enough on their own.

**Backups**

- Nightly `pg_dump` per database, using one shared script, sent to Cloudflare R2 for both projects, with a retention window.
- Restore test once a month into a scratch database. A backup that hasn't been restored doesn't count.

**Failure handling**

- If the VM is reclaimed or fails, both projects stop. Restoring from the R2 backups is the recovery path.

## Stage 2: before the first paying customer

BPA moves to its own server. Gharbaar stays on the shared free server.

**Trigger:** the first customer contract, or the first real company data loaded into any environment.

Before the move, the deployment must have been rehearsed on a scratch server. A restore from backup must have been tested there.

## Stage 3: paid server

When the paid server is bought, the BPA deployment moves to it, and Stage 2 applies to Gharbaar too if needed.

## Open items

- Confirm current Oracle Always Free limits before relying on them. They change.
- Choose Caddy or Nginx for the reverse proxy.
- Create the DuckDNS subdomains for each application.
- Create the R2 bucket for backups, one per project.
- Confirm PostgreSQL 16 packages for ARM64 on the chosen OS image.
