# BPA Platform: Data Model

Design rules that apply to every table:

- **Primary keys** are UUIDv7, so IDs are time-ordered and safe to expose in URLs. Knowing an ID never grants access.
- **Tenant scope.** Every tenant-owned table has a non-null `tenant_id` and a row-level security policy: `tenant_id = current_setting('app.tenant_id')::uuid`. The application connects with a role that does not have `BYPASSRLS`. Each transaction sets `app.tenant_id` from the authenticated session, never from a client-supplied value.
- **Platform tables** (plans, plan features, platform admins, global templates) have no `tenant_id`. The application role can read them but not modify them. Platform writes go through a separate admin role.
- **Money** is `bigint` minor units with a `currency char(3)` column. No floating point.
- **Timestamps** are `timestamptz` in UTC. Display converts to the tenant's time zone.
- **Versioned definitions** are immutable once published. Edits create a new version row.
- **Audit events** are append-only. The application role has `INSERT` and `SELECT` on `audit_events` only.
- **Idempotency.** Mutating commands carry an `idempotency_key` recorded in `command_log` with a unique constraint on (tenant_id, key).

Phase tags: each table is marked with the phase that introduces it.

## 1. Tenant and commercial

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `tenants` | id, name, status (active, suspended, archived), default_locale, default_timezone, default_currency, created_at | No (the tenant is the scope) | 1 |
| `tenant_settings` | tenant_id, key, value (jsonb) | Yes | 1 |
| `plans` | id, code, name, active | No (platform) | 1 |
| `plan_features` | plan_id, feature_code, limit_value | No (platform) | 1 |
| `subscriptions` | id, tenant_id, plan_id, status (trial, active, suspended), period_start, period_end | Yes | 1 (model), 7 (enforcement) |
| `entitlements` | id, tenant_id, feature_code, limit_value, reason, expires_at, granted_by | Yes | 7 |
| `usage_counters` | tenant_id, metric, period, value | Yes | 7 |
| `support_access_grants` | id, tenant_id, platform_user_id, reason, granted_by, starts_at, expires_at, revoked_at | Yes | 1 |
| `business_calendars` | id, tenant_id, name, working_days (bitmask), timezone, holidays (jsonb date list) | Yes | 6 |

## 2. Identity and access

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `users` | id, tenant_id, email (unique per tenant), normalized_email, password_hash (Argon2id), status (invited, active, locked, disabled), created_at | Yes | 1 |
| `invitations` | id, tenant_id, email, token_hash, role_ids, expires_at, accepted_at | Yes | 1 |
| `password_reset_tokens` | id, tenant_id, user_id, token_hash, expires_at, used_at | Yes | 1 |
| `sessions` | id, tenant_id, user_id, token_hash, created_at, last_seen_at, expires_at, revoked_at, ip_address, user_agent | Yes | 1 |
| `mfa_factors` | id, tenant_id, user_id, type, secret_ref, verified_at | Yes | 7 |
| `sso_connections` | id, tenant_id, protocol (saml, oidc), config_ref, status | Yes | 9 |
| `roles` | id, tenant_id (null for system roles), code, name, is_system | Mixed (system roles have null tenant_id) | 1 |
| `permissions` | id, code (for example `request.approve`), description | No (platform) | 1 |
| `role_permissions` | role_id, permission_id | Via role | 1 |
| `role_assignments` | id, tenant_id, user_id, role_id, scope_type (company, branch, department, project, own, assigned), scope_id, starts_at, ends_at | Yes | 1 |
| `security_events` | id, tenant_id, user_id, event_type, ip_address, created_at | Yes | 1 |

Rules:
- Email is unique within a tenant, not globally. The same person can have accounts in two companies.
- A user belongs to one tenant in the MVP. The schema allows multiple tenant memberships to be added in Phase 9 without a rewrite.
- Scope checks happen in the policy layer, against `role_assignments` and organization membership.

## 3. Organization and workforce

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `branches` | id, tenant_id, name, code, timezone, status | Yes | 1 |
| `departments` | id, tenant_id, branch_id (nullable), name, code, parent_department_id, head_position_id, status | Yes | 1 |
| `positions` | id, tenant_id, title, department_id, grade | Yes | 1 |
| `employees` | id, tenant_id, user_id (nullable until invited), employee_code, full_name, status (active, inactive, suspended, former), hired_on, left_on | Yes | 1 |
| `employee_positions` | id, tenant_id, employee_id, position_id, primary (boolean), starts_at, ends_at | Yes | 1 |
| `reporting_lines` | id, tenant_id, employee_id, manager_employee_id, starts_at, ends_at | Yes | 1 |
| `projects` | id, tenant_id, code, name, branch_id, status, starts_at, ends_at | Yes | 1 |
| `project_members` | id, tenant_id, project_id, employee_id, role_in_project | Yes | 1 |
| `absences` | id, tenant_id, employee_id, kind (leave, manual), starts_at, ends_at, substitute_employee_id | Yes | 1 (manual), 4 (leave link) |
| `group_memberships` | id, tenant_id, group_id, employee_id | Yes | 1 |
| `groups` | id, tenant_id, name, purpose (approval, procurement, receiving, admin_queue, custom) | Yes | 1 |

Rules:
- An employee can belong to several departments, branches, and projects through `employee_positions` and `project_members`.
- `reporting_lines` is effective-dated, so routing for an old request uses the manager at the time it was submitted.
- Approval routing reads `reporting_lines` as of the request's submission time for existing tasks, and as of the current time for new ones.

## 4. Forms

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `forms` | id, tenant_id, code, name, category, status (draft, published, archived) | Yes | 2 |
| `form_versions` | id, tenant_id, form_id, version_number, schema (jsonb), published_at, published_by | Yes | 2 |
| `form_sections` | id, tenant_id, form_version_id, key, title, order_index | Yes | 2 |
| `form_fields` | id, tenant_id, form_version_id, section_id, key, type, label_text, help_text, default_value, required_rule, visibility_rule, validation (jsonb), options (jsonb), order_index | Yes | 2 |
| `form_line_definitions` | id, tenant_id, form_version_id, key, min_rows, max_rows, columns (jsonb) | Yes | 2 |
| `field_groups` | id, tenant_id, name, fields (jsonb) | Yes | 2 |
| `form_templates` | id, platform template reference, tenant_id (null for platform) | Mixed | 4 |

Rules:
- Field types: text, long_text, number, decimal, currency, date, time, dropdown, multi_select, checkbox, radio, file, image, signature, employee_reference, department_reference, project_reference.
- Form labels are stored as text in the language the company wrote them in. UI chrome uses translation keys.
- Published form versions are immutable. The schema is checked for valid field keys, option references, and rule references when published.

## 5. Workflow design

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `workflow_definitions` | id, tenant_id, code, name, category, status (draft, published, deactivated), current_version_id | Yes | 3 |
| `workflow_versions` | id, tenant_id, workflow_definition_id, version_number, form_version_id, published_at, published_by, is_immutable | Yes | 3 |
| `workflow_steps` | id, tenant_id, workflow_version_id, key, type (submit, approval, task, verification, notification, completion), assignee_rule (jsonb), condition_rule_id, order_index, deadline_policy (jsonb), editable_field_keys (jsonb), return_policy (jsonb) | Yes | 3 |
| `workflow_transitions` | id, tenant_id, workflow_version_id, from_step_key, action, to_step_key, guard_rule_id | Yes | 3 |
| `rules` | id, tenant_id, workflow_version_id, expression (jsonb tree of conditions, AND/OR groups), created_at | Yes | 3 |
| `workflow_settings` | id, tenant_id, workflow_version_id, return_restart_policy (first_step or current_step), duplicate_check_days, self_approval_policy | Yes | 3 |
| `workflow_templates` | id, platform template reference, tenant_id (null for platform) | Mixed | 4 |

Rules:
- A published workflow version is immutable. Requests store `workflow_version_id` and `form_version_id` at submission.
- Rule expressions are a JSON tree, not code. Operators: equals, not equals, greater than, less than, in, is empty. Groups: AND, OR. The tree is validated on publish.
- Publishing validates: every step is reachable, every approval step has at least one resolvable assignee, and every path reaches a completion step.

## 6. Requests, instances and tasks

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `requests` | id, tenant_id, request_number, workflow_version_id, form_version_id, requester_employee_id, status (draft, submitted, in_approval, returned, rejected, cancelled, procurement, receiving, completed, closed_short), current_step_key, submitted_at, completed_at, estimated_total_minor, estimated_total_currency, branch_id, department_id, project_id, version (for optimistic locking) | Yes | 4 |
| `request_data` | tenant_id, request_id, form_version_id, values (jsonb), submitted_at | Yes | 4 |
| `request_lines` | id, tenant_id, request_id, line_key, row_index, description, unit, quantity_requested, quantity_approved, quantity_procured, quantity_received, quantity_issued, unit_price_minor, line_total_minor, currency, status | Yes | 4 |
| `numbering_sequences` | id, tenant_id, code, format (for example `PR-{year}-{seq:4}`), next_value, reset_policy | Yes | 1 (infrastructure), 4 (use) |
| `step_instances` | id, tenant_id, request_id, step_key, status (pending, active, completed, skipped, cancelled), started_at, completed_at | Yes | 4 |
| `tasks` | id, tenant_id, request_id, step_instance_id, title, instructions, assignee_type (user, group), assignee_id, status (open, claimed, completed, returned, cancelled, expired), priority, due_at, claimed_by, completed_at, version | Yes | 4 |
| `task_decisions` | id, tenant_id, task_id, request_id, decision (approve, reject, return, complete), actor_employee_id, comment, decided_at, is_substitute, substitute_for_employee_id | Yes | 4 |
| `request_transitions` | id, tenant_id, request_id, from_status, to_status, action, actor_type (user, system), actor_id, created_at | Yes | 4 |
| `comments` | id, tenant_id, target_type (request, task), target_id, author_id, body, visibility, created_at | Yes | 4 |
| `delegations` | id, tenant_id, grantor_employee_id, substitute_employee_id, starts_at, ends_at, scope | Yes | 4 |
| `saved_views` | id, tenant_id, owner_user_id, name, entity, filter (jsonb), sort (jsonb), visibility (private, shared) | Yes | 6 |
| `bulk_operations` | id, tenant_id, requested_by, action, target_query (jsonb), status, progress, created_at | Yes | 6 |

Rules:
- `requests.version` and `tasks.version` implement optimistic locking. A decision that arrives against an old version is rejected.
- A task can have at most one recorded decision. The unique constraint on (tenant_id, task_id) for `task_decisions` enforces this, which resolves concurrent approvals.
- `request_lines.quantity_*` columns are kept per line so partial fulfilment is queryable without rebuilding history.
- `delegations` supports substitutes linked to approved leave via `absences`.

## 7. Leave (template data)

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `leave_types` | id, tenant_id, code, name, paid, requires_balance (boolean, default false), requires_attachment, min_notice_days | Yes | 4 |
| `leave_entitlements` | id, tenant_id, employee_id, leave_type_id, year, allowance_days | Yes | 4 |
| `leave_ledger` | id, tenant_id, employee_id, leave_type_id, request_id, entry_type (accrual, deduction, adjustment, reversal), days, created_at, reason | Yes | 4 |

Rule: balance checks run only when `requires_balance` is true on the leave type. Approval writes a ledger deduction only in that case.

## 8. Files and documents

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `files` | id, tenant_id, storage_key (prefixed by tenant_id), original_name, content_type, size_bytes, sha256, uploaded_by, uploaded_at, scan_status (pending, clean, infected, not_scanned), deleted_at | Yes | 1 (infrastructure), 5 (attachments) |
| `file_links` | id, tenant_id, file_id, target_type (request, task, comment, form_field), target_id, created_by, created_at | Yes | 5 |
| `generated_documents` | id, tenant_id, file_id, source_type, source_id, template_code, generated_at, generated_by | Yes | 5 |
| `document_versions` | id, tenant_id, file_id, replaces_file_id, version_number | Yes | 5 |

Rules:
- Downloads use a short-lived presigned URL, issued only after the API checks the requester's permission on the linked record.
- Storage keys include the tenant ID, and the bucket policy denies access without a presigned URL.
- A file's deletion is a soft delete. The storage object is removed only by a retention job after the retention period.

## 8a. Stock management (basic in Phase 4, full in Phase 8)

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `stock_items` | id, tenant_id, code, name, unit, category, reorder_level, status | Yes | 4 (basic), 8 (full) |
| `stock_locations` | id, tenant_id, name, branch_id, project_id (nullable), type (warehouse, site_store, transit), allow_negative | Yes | 4 (one per branch), 8 (full) |
| `stock_movements` | id, tenant_id, item_id, from_location_id (nullable), to_location_id (nullable), quantity, movement_type (receipt, issue, transfer, adjustment), request_line_id (nullable), reason, created_by, created_at | Yes | 4 (receipt, issue), 8 (transfer, adjustment, count) |
| `stock_balances` | tenant_id, item_id, location_id, on_hand_quantity, updated_at | Yes | 4 |

Rules:
- Movements are append-only. Balances are derived from movements and updated in the same transaction.
- An issue that would make a balance negative is refused unless the location allows negative stock. The material request shows the available quantity next to each line before it's approved.
- A purchase receipt for a stock item creates a receipt movement at the branch location.
- `request_lines.quantity_issued` stays as-is. Each issue creates a movement linked to that line, so history is never duplicated.

## 9. Notifications and integration plumbing

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `notifications` | id, tenant_id, recipient_user_id, type, title, body, target_type, target_id, read_at, created_at | Yes | 5 |
| `notification_templates` | id, tenant_id (null for platform default), event_type, channel (in_app, email, whatsapp, sms), locale, subject, body | Mixed | 5 |
| `notification_preferences` | id, tenant_id, user_id, event_type, channel, enabled | Yes | 5 |
| `delivery_attempts` | id, tenant_id, notification_id, channel, status (queued, sent, failed, retrying), attempt_count, last_error, next_attempt_at | Yes | 5 |
| `outbox_events` | id, tenant_id, event_type, payload (jsonb), created_at, published_at | Yes | 4 |
| `jobs` | id, tenant_id (nullable for platform jobs), job_type, payload, status (queued, running, done, failed), run_at, locked_by, locked_until, attempts, last_error | Mixed | 4 |
| `command_log` | id, tenant_id, idempotency_key, command_name, result_hash, created_at | Yes | 4 |
| `webhook_subscriptions` | id, tenant_id, url, secret_ref, event_types, status | Yes | 8 |
| `webhook_deliveries` | id, tenant_id, subscription_id, event_id, status, attempt_count, response_code, next_attempt_at | Yes | 8 |
| `api_clients` | id, tenant_id, name, scopes, key_hash, last_used_at, revoked_at | Yes | 8 |
| `integration_connections` | id, tenant_id, type, config_ref, status | Yes | 8 |
| `external_id_mappings` | id, tenant_id, connection_id, internal_type, internal_id, external_id | Yes | 8 |
| `whatsapp_contacts` | id, tenant_id, user_id, phone_e164, opted_in_at, opted_out_at | Yes | 8 |

Rules:
- `outbox_events` is written in the same transaction as the state change. A worker publishes it to notifications, webhooks, and integrations. This makes notifications and webhooks never miss a committed change, and a failed send never undoes a decision.
- `jobs` uses `FOR UPDATE SKIP LOCKED` for claiming. Jobs are idempotent by design.

## 10. Audit and reporting

| Table | Key columns | Tenant-scoped | Phase |
|---|---|---|---|
| `audit_events` | id (sequential, bigint), tenant_id, occurred_at, actor_type (user, system, platform_support, ai), actor_id, action, target_type, target_id, before (jsonb), after (jsonb), request_id, ip_address, reason, session_id | Yes | 1 |
| `report_definitions` | id, tenant_id, name, entity, columns (jsonb), filters (jsonb), owner_user_id | Yes | 6 (fixed), 9 (builder) |
| `export_jobs` | id, tenant_id, requested_by, format (csv, xlsx, pdf), definition_ref, status, file_id, created_at, completed_at | Yes | 6 |
| `dashboard_preferences` | id, tenant_id, user_id, layout (jsonb) | Yes | 6 |
| `import_jobs` | id, tenant_id, requested_by, entity, status, source_file_id, mapping (jsonb), error_report_file_id | Yes | 1 (employees, departments), 8 (extended) |

Audit event types (non-exhaustive): tenant created or suspended; user invited, activated, locked; role assigned or removed; permission changed; organization changed; form published; workflow published; workflow version migrated; request transitioned; decision recorded; task reassigned; substitute used; escalation triggered; file uploaded, downloaded, deleted; notification failed; export generated; support access granted, used, revoked; plan or entitlement changed; reopen; close short; cancel.

Rules:
- `audit_events` has no `UPDATE` or `DELETE` privilege for the application role. Retention runs under a separate role that deletes only past the policy date and writes its own audit record.
- Before and after values exclude passwords, tokens, and sensitive personal fields. Those fields are redacted at write time.
- `actor_type = ai` is reserved for Phase 9. Any AI-initiated change must be recorded with the human who accepted it.

## 11. Tenant isolation test coverage

The integration suite attempts a cross-tenant read, write, search, export, and file download for each of these tables, using identifiers from another tenant. Any success fails the build.

Tables covered: every table in sections 2 to 10 with a `tenant_id`.

## 12. Indexing principles

- Every tenant-scoped index leads with `tenant_id`.
- Request lists: (tenant_id, status, submitted_at), (tenant_id, requester_employee_id, submitted_at), (tenant_id, department_id, status).
- Task queues: (tenant_id, assignee_type, assignee_id, status, due_at).
- Audit: (tenant_id, occurred_at) and (tenant_id, target_type, target_id).
- Outbox and jobs: partial indexes on unpublished and queued rows only.

## 13. Open items handled as defaults

These are documented defaults from the blueprint's open questions. Each one is configurable per workflow where the behaviour can differ.

- Return restarts from the first step by default (`workflow_settings.return_restart_policy`).
- Duplicate check window is 7 days (`workflow_settings.duplicate_check_days`).
- Self-approval is blocked by default (`workflow_settings.self_approval_policy`).
- Escalation is 24 business hours after the deadline, and reminders run 24 hours before and on the deadline.
- Business hours default to Monday to Friday in the tenant's time zone, configurable in `business_calendars` (added in Phase 6 with the calendar settings; the default is hard-coded until then).
