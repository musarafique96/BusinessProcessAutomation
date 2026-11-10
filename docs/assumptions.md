# BPA Platform: Assumptions Register

Each assumption has a default that the system uses out of the box. Where the default can differ between companies, the system makes it configurable, so no code change is needed.

Status values:
- **Assumed**: our default, not yet checked with a real company.
- **Confirmed**: checked with a real company.
- **Changed**: a real company needed something different; the default was updated.

| ID | Area | Assumption | Default | Configurable by admin | Status |
|---|---|---|---|---|---|
| A1 | Purchase approval | One manager approves a purchase request | Requester's line manager (from org hierarchy) | Yes: any user, group, or hierarchy level per step | Assumed |
| A2 | Purchase approval | One finance approver handles high-value purchases | Finance group, any one member can approve | Yes: group, single user, or several approval levels | Assumed |
| A3 | Purchase approval | Finance approval is needed above a threshold | PKR 100,000 (per workflow) | Yes: amount, category, project, or branch rules | Assumed |
| A4 | Leave | Each employee gets a fixed number of leave days per year | Fixed annual allowance per leave type | Yes: allowance can vary by designation, leave type, or year; carry-forward rules optional | Assumed |
| A5 | Leave | Leave balances are enforced | Off (balance check disabled by default) | Yes: enabled per leave type | Assumed |
| A6 | Approvals | An approver who is absent is replaced | Substitute chosen by approver; escalation to next manager after 24 business hours | Yes: substitute list, escalation time | Assumed |
| A7 | Approvals | Requesters can't approve their own requests | Blocked; routes to next manager up | Yes: per workflow | Assumed |
| A8 | Returns | A returned request restarts from the first step | Restart from first step | Yes: restart from current step | Assumed |
| A9 | Rejection | Rejection is final | Final; requester can copy into a new request | Yes: admin can reopen with reason | Assumed |
| A10 | Cancellation | Requester can cancel until the first decision | Allowed before any decision | Yes: policy per workflow | Assumed |
| A11 | Duplicates | Identical requests within 7 days are flagged | Warning shown; requester confirms | Yes: window length, or off | Assumed |
| A12 | Deadlines | Deadlines use business hours (Monday to Friday, Asia/Karachi) | Mon to Fri | Yes: working days, holidays, time zone | Assumed |
| A13 | Reminders | Reminder 24 hours before deadline and on the deadline | Two reminders | Yes: timing per workflow | Assumed |
| A14 | Timeouts | The system never approves or rejects on a timeout | Never auto-decides | No: this is a fixed rule to keep decisions with people | Assumed |
| A15 | Request numbers | Requests are numbered per company | Format such as `PR-{year}-{seq}` | Yes: format per request type | Assumed |
| A16 | Items | Purchase lines hold free-text item descriptions | Free text | Yes: item catalogue can be added later | Assumed |
| A17 | Material issue | Stock balances are tracked from Phase 4, so issues can't exceed available stock. Phase 8 adds multiple locations, transfers, and reorder rules | Basic balance per item and location from Phase 4; issues blocked when stock is insufficient unless the location allows negative stock | Yes: stock locations, item catalogue, negative stock allowed, and reorder rules | Changed (scope confirmed by owner) |
| A18 | Users | A user belongs to one company | One company per user | No (multi-company access is a Phase 9 change) | Assumed |
| A19 | Users | Email addresses are unique within a company | Unique per company | No | Assumed |
| A20 | Language | Interface in English; form labels in the company's own language | English UI | Yes: Urdu and Arabic in later phases | Assumed |
| A21 | Connectivity | Users are online | Online only | Offline mode in Phase 8 | Assumed |
| A22 | Devices | Field users can take photos from a browser | Camera input on mobile browsers | Dedicated app in Phase 8 | Assumed |
| A23 | Files | Attachments up to 10 MB each, 100 MB per request | 10 MB / 100 MB | Yes: size and type limits per company | Assumed |
| A24 | Volumes | A company creates about 500 requests per month | 500 per month, used as the capacity planning default | Yes: expected volume is a per-company setting; the system does not block higher volumes | Assumed |
| A25 | Plans | Limits are on active users and active workflows | Users and workflows counted | Yes: plan definitions | Assumed |
| A26 | E-signatures | Built now as a product feature | Available as a feature; labelled as a feature | Yes: enabled per company | Assumed |
| A27 | E-signatures | Legal validity is confirmed before the product calls signatures legally binding | Not claimed until review is done | Product wording set after legal review | Assumed |

## How to use this register

- When a real company is onboarded, check each assumption with them and update its status.
- A "Changed" status means the default was wrong for that company. Decide whether the default should change for everyone or stay as a company setting.
- Assumptions A1 to A6 are the most important to confirm, because they affect the core approval flow.
