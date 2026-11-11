# BPA Platform: Phase Plan

Scope: all 22 modules of the blueprint (4.1 to 4.22), delivered in phase order. Nothing is excluded. Each phase ends with an exit gate: its capabilities are built, permission-checked, tested, and documented before the next phase starts.

Phase order follows dependencies. A later phase may start early only where it has no dependency on unfinished work.

## Phase 0: Assumptions and process definition

No external interviews. Output is a written register instead.

- `docs/assumptions.md`: every assumption made about processes, roles, forms, approvals, volumes, and devices, each with a status (assumed, to be verified).
- Reference processes written from the blueprint: purchase, material request and issue, leave (see `reference-purchase-request.md`).
- Sample data prepared for each template: forms, approval matrices, leave policies, item lists.

Exit gate: the assumptions register and reference processes are reviewed and accepted.

## Phase 1: Platform foundation and security

Modules: 4.1 Platform and tenant management, 4.2 Authentication (username and password, Gmail sign-in, invitations, reset, sessions). Phone sign-in is added later, because it needs a paid SMS or WhatsApp provider. Usernames, emails and phone numbers are unique within a company, and a username can't change once created., 4.3 Organization and workforce, 4.4 Roles, permissions and access scopes (including absence-based substitutes), 4.14 Audit trail (infrastructure), 4.15 Plan data model, 4.18 Administration (settings, calendars, numbering), 4.19 Localization framework.

Also built here: tenant isolation through row-level security, the tenant-aware file storage foundation, time-limited support access with audit, the design system and error handling.

Exit gate: a tenant can be created; users are invited and sign in; roles scope access correctly; the cross-tenant test suite passes for every module built so far.

Deferred items to pick up (decided, not yet built):
- Email delivery for invitations and password resets. Needs a chosen email provider, a sending domain with DNS records (SPF, DKIM), and a secret key on the server. Until then, admins share the links.
- Revoke sessions on password reset. Sessions are cookies valid for up to 12 hours, so a reset doesn't sign out an existing login yet. Fix before the first customer.
- Self-service password reset by email (depends on email delivery above).
- Gmail sign-in (needs a Google OAuth client ID).
- Roles and permissions API, then tenant provisioning.

## Phase 2: Form builder and form runtime

Modules: 4.5 Dynamic form builder.

Includes all field types from the blueprint, repeatable line items, conditional visibility and required rules, field-level rights per workflow step, form versions, and form preview on desktop and mobile.

Exit gate: an admin publishes a form with line items; an employee fills it, and invalid data is rejected with clear messages.

## Phase 3: Workflow definition

Modules: 4.6 Workflow definition builder (step-list editor, not canvas), including validation on publish.

Includes sequential steps, approval and task step types, user, role, group, and hierarchy assignment, conditional branches, deadlines and reminder configuration, and immutable published versions.

Exit gate: a workflow with an unreachable step, a missing approver, or no completion path cannot be published.

## Phase 4: Execution, requests, tasks and approvals

Modules: 4.7 Workflow execution engine, 4.8 Request and case management (core), 4.9 Task and work management, 4.10 Approval and decision management (sequential and any-one-of group patterns, self-approval prevention), 4.16 Search and filters (basic), 4.21 Templates (purchase, material request and issue, leave, installable from the platform).

Also in this phase: basic stock balances, so material issues can't exceed what's in stock. This covers one stock location per branch, item records, receipts from the purchase receipt step, issues from material requests with an availability check, and a running balance per item and location.

Exit gate: the three templates run end to end, from submission to completion, with tasks, decisions, and full history. Every reference-process acceptance test passes.

## Phase 5: Notifications and documents

Modules: 4.11 Notifications (in-app and email, templates, preferences, reminders, overdue alerts, escalation, delivery tracking), 4.12 Document management (attachments, permission-checked downloads and previews, PDF generation for request summaries and receipts, malware scanning where the storage provider supports it).

Exit gate: each event produces the correct alert; delivery failures are visible and retried; a notification failure never reverses a recorded decision.

## Phase 6: Dashboards, reporting and operations

Modules: 4.13 Dashboard and operational reporting (role dashboards, fixed reports, CSV and Excel and PDF exports, asynchronous large exports, stock reports, low-stock alerts), 4.14 Audit trail (search and export UI), 4.16 Search and filters (saved views), 4.8 Request management (bulk actions), 4.18 Administration (remaining tenant settings and numbering formats), 4.21 Template catalogue (preview, versioned template updates kept separate from customer copies).

Exit gate: every metric reconciles with the underlying records; exports respect permissions; saved views and bulk actions pass the tenant isolation suite.

## Phase 7: Hardening and operational readiness

Modules: 4.15 Subscriptions and entitlements (limits enforced in the backend, usage meters, trial tenants, manual plan assignment), 4.2 Authentication (multi-factor authentication).

Also: load testing, security review, backup and restore drills, monitoring and alerting, incident runbooks, and the full product acceptance checklist from the blueprint.

Exit gate: the platform meets the non-functional requirements in section 12 of the blueprint, and a restore from backup has been tested.

## Phase 8: Integrations and channels

Modules: 4.17 Integrations and API management (versioned APIs, scoped API credentials, webhooks, integration logs, external ID mapping, CSV and Excel import improvements), 4.11 Notifications (WhatsApp Business and SMS), 4.20 Mobile and field operations (dedicated mobile app, push notifications, offline drafts and queued actions with sync and conflict handling).

Built in the blueprint's order. The first integration (accounting, ERP, HR, or inventory) is chosen at the start of this phase.

The mobile app is built with Flutter, the same framework as the Gharbaar mobile app. Before Phase 8, the responsive web app works in phone browsers.

Push notifications use Firebase Cloud Messaging. Gharbaar's plan uses the same service, but its current code has no push setup yet (checked against its repository, `musarafique96/household-app`). Check the BPA push design against Gharbaar's working version once it exists. The BPA backend sends each message through Firebase, and each phone's device token is stored in the BPA database. Sending to iPhones needs a paid Apple Developer account.

Also in this phase: full stock management, added to the scope at the owner's request. It extends the Phase 4 balances with multiple stock locations, transfers between them, stock adjustments and stock counts, and reorder rules.

Exit gate: webhooks deliver with retries and signatures; WhatsApp and SMS respect opt-in and template rules; offline actions are idempotent and conflict-safe.

## Phase 9: Advanced automation and ecosystem

Modules: 4.22 AI and intelligent automation (document extraction with human verification, workflow suggestions, summaries, classification; never silent high-impact decisions), 4.21 Template marketplace (paid and partner templates, versioned updates), 4.2 Enterprise single sign-on (SAML and OIDC), 4.13 Report builder and custom dashboards and advanced analytics, 4.6 Visual canvas designer, 4.10 Parallel approvals and quorum patterns, 4.6 Reusable sub-workflows and advanced expressions, 4.12 Legally binding e-signatures, 4.14 Compliance exports and tamper-evident audit storage, multi-region operation.

E-signatures are built as a product feature in this phase. The product does not call them legally binding until a legal review confirms validity in each jurisdiction where they're used (see assumptions A26 and A27).

Exit gate: each module passes its own tests, and the core execution, permission, and audit model is unchanged.

## Phase 10: Attendance, shifts and leave allocations

Scope added at the owner's request. These modules are not in the 22-module blueprint, so the coverage table below lists them separately. Role-based access applies throughout.

- **Attendance recording, three methods, all built:**
  - Manual entry by an authorized person, with a reason and an audit record for each change.
  - Location check-in: the employee checks in or out from the phone browser or app. The app reads the device location once, at the moment of check-in or check-out, and compares it with the site's allowed radius. Only the result, the time, and the accuracy reading are stored. Location is never tracked between check-ins. Employees consent before the first use, and a manual fallback is always available when location is unavailable or denied.
  - Biometric device: a device adapter interface is built now, with a simulator for tests. Vendor integration is added when the hardware is bought.
- Shifts, working-hour rules, and business calendars (holidays per company).
- **Leave allocations:** entitlements per leave type, accrual and carry-forward rules, and a ledger for every change. Fixed annual allowance is the default. Dynamic rules are configurable per company (A4, A5).
- **Overtime:** rules configurable per company (rates, thresholds, approval chain). A specialist reviews each rule set before it is used for money calculations (see money rules below).

Exit gate: each attendance method records correctly and respects its consent rules; leave balances reconcile with the ledger; overtime rules reproduce the reviewed reference calculations.

## Phase 11: Employee services and sensitive HR records

Modules: helpdesk tickets, business trip requests and settlements, training records and obligations, performance evaluations, and the employee self-service portal.

- Sensitive data (evaluations, training obligations, attendance, loans) has its own field-level rules. Only the roles named in the policy can read or edit each field, and every read of sensitive data is audited.
- Evaluations and their scores are visible only to the reviewer chain and HR, not to peers or line managers outside that chain.
- Trip settlements and expense claims use the same per-company money rules as other requests.

Exit gate: field-level access tests pass for every sensitive field; a user outside the policy cannot read or export a sensitive value through any screen, API, or report.

## Phase 12: Payroll and compensation

Modules: loans and advances (schedules, deductions, early settlement), payroll-linked items (overtime, allowances, deductions, reimbursements), payroll runs with payslips, and payroll documents.

- Money rules are per-company settings: rates, rounding, deduction order, and tax tables. The product ships no default tax rule. A specialist validates each rule set before it is used for payroll, and the validated version is recorded with its reviewer and date.
- Payroll calculations are reproducible: the same inputs and rule version always give the same result.
- Payslips are permission-checked documents, consistent with Phase 5.

Exit gate: payroll runs reconcile with the attendance, leave, overtime, and loan ledgers; the reference payroll calculations match the validated rule set; payslips are visible only to the employee and authorized HR roles.

## UI and design standard (applies to every phase)

- **Light and dark themes from Phase 1.** Colors come from design tokens, not hard-coded values, so both themes are built from the same components.
- **Both themes are tested.** Every screen is checked in light and dark, including contrast for text, status colors, and charts.
- **Polished, not busy.** Consistent spacing, typography, and status colors. Subtle motion for feedback, never decoration that slows work.
- **Accessible.** Keyboard navigation, screen-reader labels, and WCAG AA contrast.
- **Right-to-left ready.** Layouts use logical properties so Urdu and Arabic work without redesign.
- **Responsive.** Every screen works on desktop, tablet, and phone browsers.
- **Visual regression tests.** Screenshots of key screens in both themes are compared on each change, so the design doesn't drift.

## Module coverage check

| Module | Phases |
|---|---|
| 4.1 Platform and tenant management | 1 |
| 4.2 Authentication and account security | 1, 7, 9 |
| 4.3 Organization and workforce | 1 |
| 4.4 Roles, permissions and scopes | 1 |
| 4.5 Dynamic form builder | 2 |
| 4.6 Workflow definition builder | 3, 9 |
| 4.7 Workflow execution engine | 4 |
| 4.8 Request and case management | 4, 6 |
| 4.9 Task and work management | 4 |
| 4.10 Approval and decision management | 4, 9 |
| 4.11 Notifications and communication | 5, 8 |
| 4.12 Document and evidence management | 5, 9 |
| 4.13 Dashboard and reporting | 6, 9 |
| 4.14 Audit trail | 1, 6, 9 |
| 4.15 Subscriptions and entitlements | 1, 7 |
| 4.16 Search, filters and saved views | 4, 6 |
| 4.17 Integrations and API | 8 |
| 4.18 Administration and configuration | 1, 6 |
| 4.19 Localization | 1 |
| 4.20 Mobile and field operations | 8 |
| 4.21 Templates and marketplace | 4, 6, 9 |
| 4.22 AI and intelligent automation | 9 |

HR/ERP extension (outside the 22-module blueprint, added at the owner's request): attendance, shifts and business calendars, leave allocations, and overtime (Phase 10); helpdesk, business trips, training, evaluations, and the employee portal (Phase 11); loans, payroll-linked items, and payroll runs (Phase 12).
