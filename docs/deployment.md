# BPA Deployment Plan

Status: plan agreed for development. Infrastructure is not set up yet. Operational readiness is Phase 7 (see `phase-plan.md`).

## Stage 1: development (now)

One Oracle Cloud Always Free Ampere A1 virtual machine, shared with the Gharbaar project.

**Isolation rules, from day one**

- Separate PostgreSQL databases per project (`bpa` and `gharbaar`), each with its own login role. Neither role has rights on the other's database.
- The BPA application connects as the `bpa_app` role, which has no `BYPASSRLS`, so row-level security applies in production as it does in tests.
- Each application runs as its own system user with its own environment file. Secrets stay out of the repository.
- PostgreSQL listens on localhost only. Only ports 80, 443 and SSH are open to the internet.
- A reverse proxy (Caddy or Nginx) terminates TLS and routes each DuckDNS subdomain to its application.

**Backups**

- Nightly `pg_dump` per database, written to Oracle Object Storage (free tier), with a retention window.
- Restore test once a month into a scratch database. A backup that hasn't been restored doesn't count.

**Failure handling**

- If the VM is reclaimed or fails, both projects stop. Restoring from the Object Storage backups is the recovery path.

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
- Create the Object Storage bucket for backups.
- Confirm PostgreSQL 16 packages for ARM64 on the chosen OS image.
