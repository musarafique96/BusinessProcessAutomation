# BPA Platform: UI Direction

Status: proposal for review. Based on the nine reference designs supplied by the owner. Nothing here is built yet.

## 1. What the references have in common

These patterns appear in most of the references, so the design should use them:

- **Left sidebar navigation** with icons, labels, and count badges (for example, Requests 3, Tasks 5). The active item is clearly highlighted. The sidebar collapses to icons on smaller screens.
- **Top bar** with a global search (keyboard shortcut Ctrl+K), a notification bell with a count, a language switch, a company switcher, and a user menu.
- **KPI cards** with a coloured icon tile, a large number, a percentage change with an arrow, and a small trend line.
- **Charts** that are smooth and readable: a line or area chart for trends, a donut for department share, and bars for ranking. Each chart has a time-range toggle (7D, 30D, 90D, 1Y) and a hover tooltip.
- **Activity feed** with a coloured status icon, a one-line summary, and a relative time ("2 min ago").
- **Tables with status tabs** showing counts (All 248, Pending 42, Approved 176), filters, colour-coded status pills, row actions, and "Showing 1–5 of 248" pagination.
- **Request detail** with a header and status pill, tabs (Details, Approval History, Attachments, Comments), and a vertical approval flow on the right showing who has acted and who's waiting.
- **Workflow designer** with a canvas of nodes (start, approval, condition, end), a palette of step types on the left, a settings panel on the right, and Validate, Save, and Publish in the toolbar.
- **Mobile** with a bottom tab bar (Home, Tasks, Notifications, Profile), a quick-action grid, and a recent activity list.
- **Settings** organized into tabs (Company, Users and permissions, Approval rules, Localization, Subscription).

## 2. What to leave out

Several references include elements that suit a marketing site, not a working tool:

- "Upgrade to Pro" and "Learn more" promotional cards in the sidebar and dashboard. Plan usage belongs in the Subscription settings, with a quiet usage meter.
- Decorative stock photos of construction sites on the dashboard. They take space from data. If a company wants a banner, it should be optional and dismissible.
- Gradient text and glowing borders. They're hard to read in dark mode and make the interface look busy.
- Tiny grey text on pastel or gradient cards. It fails contrast checks in light mode.

## 3. Design principles

1. **Data first.** The dashboard answers "what needs my attention" before "how is the company doing."
2. **One accent colour.** Blue is the action colour. Green, amber, red, and grey are reserved for status meaning only.
3. **Readable in both themes.** Every colour is a design token with a light and a dark value. Contrast meets WCAG AA in both.
4. **Right-to-left ready.** Urdu and Arabic mirror the layout: the sidebar moves to the right, arrows flip, and progress flows right to left. Several references are left-to-right only, so this has to be designed in from the start.
5. **Calm density.** Enough white space to scan, enough information to act without drilling down.
6. **Consistent status language.** Pending, Approved, Rejected, Returned, Completed, and Cancelled use the same colours and icons on every screen.

## 4. Proposed tokens (starting point)

| Token | Light | Dark |
|---|---|---|
| Background | Soft grey-blue | Deep navy |
| Surface (cards) | White | Slate |
| Primary (action) | Blue | Lighter blue |
| Success | Green | Green |
| Warning | Amber | Amber |
| Danger | Red | Red |
| Text primary | Near-black | Near-white |
| Text secondary | Mid grey (4.5:1 contrast minimum) | Light grey (4.5:1 contrast minimum) |
| Corner radius | 12px cards, 8px controls | Same |
| Elevation | Soft, single shadow | Border instead of shadow |

Typography: one sans-serif family with Latin and Arabic script support, and a clear type scale (page title, section title, card number, body, caption).

## 5. Key screens, to agree first

Most modules share these, so we agree on them before anything else:

1. **Dashboard:** greeting, four KPI cards, request trend chart with time-range toggle, department donut, my tasks, recent activity.
2. **Requests list:** status tabs with counts, search, filters, table, pagination, and a New Request button.
3. **Request detail:** header and status, tabs, details panel, approval flow timeline, and the primary action button.
4. **Workflow designer:** step palette, canvas, settings panel, and a Validate and Publish toolbar.
5. **Mobile home and tasks:** bottom tab bar, quick actions, and task list with approve and review actions.

## 6. Chart standard

- One palette for categories, used consistently across all charts.
- Every chart has a title, a time range, a legend, and a tooltip.
- Empty states say what the chart will show once there's data.
- Charts are readable without colour alone, so each series also has a shape or label.
- Charts are exported as images or data in the Reports module.

## 7. Phase 9 placeholders

The AI features and governance panels in the references (AI builder, AI copilot, governance and control card) are shown as placeholders in the design only. They're built in Phase 9, after human review rules are in place.

## 8. Next steps

1. Review this direction and choose the references to follow for the dashboard and request screens.
2. Build a clickable prototype of the dashboard and request detail, in light and dark, in both LTR and RTL.
3. Test the prototype with the owner and then with the first real company before building the full screens.
