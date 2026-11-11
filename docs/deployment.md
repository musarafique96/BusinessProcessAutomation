# BPA Deployment Plan

Status: plan agreed for development. Infrastructure is not set up yet. Operational readiness is Phase 7 (see `phase-plan.md`).

**Decided:** deployment uses Docker Compose, with Caddy handling HTTPS. This is the same method Gharbaar uses.

## Stage 1: development (now)

One Oracle Cloud Always Free Ampere A1 virtual machine, called `appserver` (planned name; the rename from the old working name isn't done yet). It runs both the BPA and Gharbaar projects.

```
appserver (Oracle, Always Free Ampere A1)
├── Edge: Caddy (ports 80 and 443)
│   ├── gharbaar.duckdns.org → Gharbaar API
│   └── bpaplatform.duckdns.org      → BPA API
├── Gharbaar
│   └── Gharbaar API → Gharbaar PostgreSQL database (name: gharbaar)
└── BPA
    └── BPA API → BPA PostgreSQL database (name: bpa)
```

Gharbaar's notes plan to switch from DuckDNS and Caddy to a Cloudflare Tunnel later. That change affects only the edge. Both projects keep their own API and database.

**Repositories.** All three repositories live under the same GitHub account. Each one has its own folder on the server.

| Repository | Server folder | Holds |
|---|---|---|
| `musarafique96/BusinessProcessAutomation` (BPA) | `~/bpa-repo` | BPA API, its database, and its compose file |
| `musarafique96/household-app` (Gharbaar) | `~/gharbaar-repo` | Gharbaar API, its database, and its compose file |
| `server-infra` (new, not created yet) | `~/server-infra` | Shared edge and server setup (see below) |

**`server-infra` holds only what all apps depend on:**
- The shared Caddy configuration, with one entry per public name (`gharbaar.duckdns.org`, `bpaplatform.duckdns.org`).
- The shared Docker network that lets Caddy reach each app's API.
- Notes on the server setup that every app depends on.

**Each app repository keeps only its own parts:** its API, its database, and its compose file. Each app's compose file joins the shared network, so Caddy can route to its API. No app repository contains Caddy configuration.

**Same approach for both projects.** Each project has its own database, but they're run and backed up the same way.

- **Containers:** each project runs under its own Docker Compose file in its own directory. Each has its own PostgreSQL 16 container and its own data volume, so `docker compose down -v` in one project never touches the other. The BPA application also runs in a container.
- **Isolation:** separate databases (`bpa` and `gharbaar`), each with its own login role. Neither role has rights on the other's database. The BPA application connects as `bpa_app`, which has no `BYPASSRLS`, so row-level security applies in production as it does in tests.
- **Network:** PostgreSQL listens only on the internal Docker network. Only the application container can reach it. Only ports 80, 443 and SSH are open to the internet.
- **Routing and TLS:** Caddy terminates TLS and routes each DuckDNS subdomain to its application, matching the Gharbaar setup.
- **Secrets:** each project keeps its secrets in a file on the server, outside the repository.
- **Schema changes:** one migration runner, shared by both projects, applies migration files in order and records which ones have run. It lives in the `server-infra` repository, which is created from the Gharbaar chat, along with the server layout handover. Container init scripts run only on a fresh volume, so they aren't enough on their own.

**Backups**

- Nightly `pg_dump` per database, using one shared script, sent to Cloudflare R2 for both projects, with a retention window.
- Restore test once a month into a scratch database. A backup that hasn't been restored doesn't count.

**Failure handling**

- If the VM is reclaimed or fails, both projects stop. Restoring from the R2 backups is the recovery path.

### BPA deploy steps (first deploy and each update)

Files are in `deploy/`: `Dockerfile` (API image), `docker-compose.yml` (database, migrations, API), `migrate.sh` (interim migration runner), `create_login_role.sql` (API login role), `.env.example`.

Prerequisites on the server: Docker with the Compose plugin, the shared `edge` Docker network (created by `server-infra`, with a fixed subnet and Caddy at `CADDY_ADDRESS`), and a DuckDNS name for BPA.

1. Clone the repo to `~/bpa-repo`, then `cd ~/bpa-repo/deploy`.
2. `cp .env.example .env`, then set `POSTGRES_PASSWORD` and `BPA_APP_LOGIN_PASSWORD` to two different long random values, and `CADDY_ADDRESS` to Caddy's address on `edge`. Keep `.env` out of git.
3. `docker compose --env-file .env up -d --build`. The `migrate` service applies each migration once (recorded in `schema_migrations`), creates or updates the `bpa_login` role, and exits. The `api` service starts after it succeeds.
4. Caddy in `server-infra` routes `bpaplatform.duckdns.org` to `bpa-api:8080`.
5. Check: `curl https://bpaplatform.duckdns.org/health` returns `{"status":"ok"}`.

Updates: `git pull`, then step 3 again. Migrations that already ran are skipped.

Backups of the `bpa` database are part of the shared nightly `pg_dump` (not set up yet). Restoring the session-key volume `bpa-keys` is needed too, or everyone is signed out.

## Stage 2: before the first paying customer

BPA moves to its own server. Gharbaar stays on the shared free server.

**Trigger:** the first customer contract, or the first real company data loaded into any environment.

Before the move, the deployment must have been rehearsed on a scratch server. A restore from backup must have been tested there.

## Stage 3: paid server

When the paid server is bought, both the BPA and Gharbaar systems move to it together. The Cloudflare Tunnel switch happens at the same time. Each project keeps its own API and database.

**Region note:** the Oracle account's home region is Jeddah. Development and testing from Pakistan, Saudi Arabia and Bahrain run from there. Confirm before buying the paid server whether its region can differ from the home region.

## Open items

- Confirm current Oracle Always Free limits before relying on them. They change.
- Choose Caddy or Nginx for the reverse proxy.
- Create the DuckDNS subdomains for each application.
- Create the R2 bucket for backups, one per project.
- Confirm PostgreSQL 16 packages for ARM64 on the chosen OS image.
