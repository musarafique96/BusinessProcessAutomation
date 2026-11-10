# Reference Process: Purchase Request

This is the reference implementation for the engine. Every workflow capability must work for this process before it's considered done. Where the blueprint leaves a behaviour open, the default below applies and is configurable per workflow.

## 1. Purpose and actors

Control a purchase from employee need through approval, procurement, receipt, and closure.

| Actor | Role |
|---|---|
| Requester | Employee who submits the request |
| Line manager | Approves first, resolved from the organization hierarchy |
| Finance approver | Member of the finance group; approves when the condition is met |
| Procurement operator | Records supplier and purchase details |
| Receiver | Records quantities received (optional step, enabled per workflow) |
| Admin | Reassigns, reopens, or closes requests; never approves on another's behalf without a recorded reason |

## 2. Request data

**Header fields (from the form):** purpose, department, project (optional), branch, required date, justification, supporting quotation (optional file).

**Line items (repeatable):** item description, unit, quantity, estimated unit price, estimated line total (computed). Money is stored as integer minor units with a currency code.

**Derived:** estimated total (sum of line totals), request number (generated from the tenant's numbering format).

**Procurement fields (set by the operator):** supplier, quotation reference, purchase order number, agreed unit price per line, expected delivery date.

**Receipt fields (set by the receiver):** received quantity per line, condition notes, evidence photo (optional).

## 3. Workflow steps

| Step | Type | Assignee | Condition | Completion |
|---|---|---|---|---|
| 1. Manager approval | Approval | Line manager (hierarchy), fallback: department head | Always | Approve, reject, or return |
| 2. Finance approval | Approval | Finance group, any one member | Estimated total > threshold, or category = capital expenditure | Approve, reject, or return |
| 3. Procurement | Task | Procurement group | After all approvals | Operator records supplier and purchase details, then completes the task |
| 4. Receipt | Task | Receiver (line's requester by default) | Enabled in workflow configuration | Receiver records received quantities, then completes the task |
| 5. Completion | System | Automatic | All lines fully received, or closed short by admin | Request marked completed; summary PDF generated |

Default threshold: configured per tenant and per workflow version. The example in the acceptance tests uses PKR 100,000.

## 4. States

| State | Type | Meaning |
|---|---|---|
| Draft | Working | Requester is still editing; no routing has happened |
| Submitted | Working | Validated and routed; first step is starting |
| In approval | Working | At approval step n |
| Returned | Waiting | Sent back to the requester with a comment |
| Rejected | Terminal | Refused at an approval step |
| Cancelled | Terminal | Withdrawn by the requester or an authorized person |
| Procurement | Working | All approvals recorded; procurement task open |
| Receiving | Working | Receipt task open; some lines may be received |
| Completed | Terminal | All lines received, or receipt step skipped and procurement finished |
| Closed short | Terminal | Admin closed with outstanding lines, with a reason |

## 5. Transitions

| From | Action | Actor | Guard | To | Side effects |
|---|---|---|---|---|---|
| Draft | Submit | Requester | Form valid; required files uploaded | Submitted | Create instance on current published version; route to step 1 |
| Submitted | Route | System | Step 1 resolved | In approval (step 1) | Create approval task; notify approver |
| In approval (step n) | Approve | Assigned approver | Not self-approval; decision comment if required | In approval (next step) or Procurement | Record decision; route next step or create procurement task |
| In approval (step n) | Reject | Assigned approver | Comment mandatory | Rejected | Close open tasks; notify requester |
| In approval (step n) | Return | Assigned approver | Comment mandatory | Returned | Notify requester; unlock editable fields |
| Returned | Resubmit | Requester | Form valid | Submitted | New form version recorded; restart from step 1 (default) |
| Returned | Cancel | Requester | None | Cancelled | Close open tasks; notify approvers |
| Draft or In approval (before any decision) | Cancel | Requester | No decision recorded yet | Cancelled | Close open tasks; notify assignees |
| In approval (after a decision) | Cancel | Admin, or the last decision's approver | Reason mandatory | Cancelled | Close open tasks; audit as cancellation |
| Procurement | Complete procurement | Operator | Supplier and PO number recorded; agreed prices per line | Receiving (if enabled) or Completed | Notify requester |
| Receiving | Record receipt | Receiver | Quantity not more than ordered per line | Receiving or Completed | Update line state; if all lines complete, complete request |
| Receiving | Close short | Admin | Reason mandatory | Closed short | Close open receipt task; notify requester |
| Any open state | Reassign task | Admin | New assignee has permission | Same state | Audit reassignment |
| Closed or completed | Reopen | Admin | Reason mandatory | Previous working state | Audit reopen; recompute open tasks |

## 6. Exception handling

**Requester edits after submission.** Not allowed. The only paths are return or cancel and resubmit.

**Approver absent.** If the approver has an absence period, tasks route to their configured substitute for those dates. With no substitute, the task escalates to the next manager up after 24 business hours.

**Manager changes during approval.** The pending task stays with the original approver until reassigned. The system creates one admin task listing the affected approvals.

**Self-approval.** If the approver would be the requester, the system routes to the next manager up. If none exists, the task goes to the admin queue.

**Duplicate submission.** If the same requester has a request with identical lines in the last 7 days, the system shows a warning. The requester confirms, and the approver sees the flag.

**Item unavailable or price changed at procurement.** The operator records the outcome per line: purchased at a different price (with reason), or not available. Unavailable lines stay open until the receiver closes them or the admin closes the request short.

**Partial receipt.** The request stays in Receiving until every line is fully received or closed. Remaining quantities are visible on the request and the dashboard.

**Permission revoked while a task is pending.** The task moves to the department's admin queue and the admin is notified.

**Workflow edited while requests are open.** Open requests stay on their version. Migration is an admin action with a preview of affected requests.

**Timeout.** A reminder 24 hours before the deadline and on the deadline. Escalation to the next manager up 24 business hours after the deadline. The system never approves or rejects on a timeout.

**Attachment failure.** A required quotation missing blocks submission only if the workflow marks it required. Optional uploads can fail without blocking.

## 7. Notifications

| Event | Recipients | Channel |
|---|---|---|
| Submitted | Requester (confirmation); first approver (task) | In-app, email |
| Approval task assigned | Assignee | In-app, email |
| Approved (intermediate) | Requester | In-app |
| Final approval | Requester; procurement group (task) | In-app, email |
| Rejected | Requester | In-app, email |
| Returned | Requester | In-app, email |
| Reminder and overdue | Assignee; escalation target when overdue | In-app, email |
| Completed | Requester | In-app, email |

## 8. Audit events recorded

Submit, route, approve, reject, return, resubmit, cancel, reassign, reopen, close short, procurement recorded, receipt recorded, substitute used, escalation triggered, form version used, workflow version used, file uploaded, file downloaded, notification sent or failed. Each event records actor (user or system), tenant, timestamp, target, and before and after values where relevant.

## 9. Acceptance tests

Each test must pass in the integration suite and as an end-to-end test in the web application.

1. **Happy path, below threshold.** Requester submits PKR 40,000. Manager approves. Procurement records supplier and PO. Receiver records full receipt. Request completes. Summary PDF attached.
2. **Happy path, above threshold.** Requester submits PKR 250,000. Manager approves. Finance approves. Procurement and receipt complete the request.
3. **Capital expenditure routing.** Requester submits a request with a capital expenditure category below threshold. Finance approval is still required.
4. **Rejection.** Manager rejects with a comment. Request is terminal. Requester can copy it into a new linked request.
5. **Rejection without comment blocked.** Manager tries to reject with an empty comment. Action fails with a validation message.
6. **Return and resubmit.** Manager returns with a comment. Requester edits an editable field and resubmits. Routing restarts from step 1 and a new form version is recorded.
7. **Return cannot change decision fields.** Requester tries to edit a field not marked editable at the return step. Edit is refused.
8. **Self-approval blocked.** A manager who is also the requester cannot approve. The request routes to the next manager up.
9. **Absence substitution.** Manager is on approved leave. Tasks route to the substitute for those dates. Tasks return to the manager afterwards.
10. **Escalation on timeout.** Approval is 24 business hours overdue. Task escalates to the next manager up. The system does not approve it.
11. **Cancellation before decision.** Requester cancels a request at step 1 with no decision recorded. Open tasks close without a reason being required.
12. **Cancellation after decision.** Requester cannot cancel after the manager has approved. Admin cancels with a reason. Audit records it.
13. **Duplicate warning.** Requester submits an identical request within 7 days. Warning shown; submission proceeds only after confirmation; approver sees the flag.
14. **Partial receipt.** Receiver records 6 of 10 units on one line and full quantity on another. Request stays in Receiving with the outstanding line visible.
15. **Close short.** Admin closes an outstanding line with a reason. Request completes as Closed short.
16. **Workflow edited mid-flight.** Admin publishes a new workflow version. Open requests continue on their original version. Migration preview lists affected requests; nothing moves without confirmation.
17. **Permission revoked.** A user loses approval permission while holding a pending task. The task moves to the admin queue and the admin is notified.
18. **Notification failure.** Email provider fails after a final approval. The approval is recorded and the request advances. The failed notification is retried and visible in the delivery log.
19. **Tenant isolation.** A user from another tenant cannot read, search, export, or download any file or record from this request, including by guessing IDs.
20. **Concurrent decisions.** Two approvers act on the same task at the same time. Exactly one decision is recorded; the second receives a conflict response.
21. **Duplicate action retry.** A network retry of an approve action with the same idempotency key does not record a second decision.
22. **Audit completeness.** Every transition in the request history has an audit event with actor, timestamp, and before and after values.
