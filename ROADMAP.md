# OpenBook — Roadmap (.NET 10)

Source: `accounting-app-feature-list.pdf` (QuickBooks alternative feature checklist).
Feature counts from the PDF: **35 MVP**, **73 V1**, **~110 LATER**.

## Stack decisions

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (LTS) | Target from the PDF |
| UI | **Blazor Web App** (Interactive Server), run locally via Kestrel | One UI serves both the MVP "local web UI" and the V1 "self-hosted / Docker" mode. Avalonia would need a second UI later. |
| API | ASP.NET Core Minimal APIs | V1 requires "everything the UI can do" over REST; UI and API call the same application layer from day one |
| Data | EF Core 10 — SQLite (MVP), PostgreSQL (V1) | Keep provider-neutral from the start: no SQLite-only SQL, migrations per provider |
| Money | `decimal` everywhere + a `Money` value object | Exact math; ban `double`/`float` in Domain via an analyzer |
| PDFs | PDFsharp-MigraDoc 6.2.4 (MIT, pinned: 6.5+ is AGPL) | Invoices, statements, checks, reports |
| Spreadsheets | ClosedXML | XLSX export |
| Testing | xUnit + Verify (snapshot reports) + property tests (FsCheck/CsCheck) for ledger invariants | |

### Solution layout

```
src/
  OpenBook.Domain          // entities, Money, posting rules, invariants (no EF, no ASP.NET)
  OpenBook.Application     // use cases / commands / queries, validation
  OpenBook.Infrastructure  // EF Core, migrations (Sqlite + Postgres), file storage, SMTP, PDF
  OpenBook.Api             // Minimal API endpoints
  OpenBook.Web             // Blazor UI
tests/
  OpenBook.Domain.Tests
  OpenBook.Application.Tests
  OpenBook.Integration.Tests   // runs against SQLite and Postgres (Testcontainers)
```

### Design rules to lock in at the start (expensive to retrofit)

1. **Every business document posts through one ledger engine.** Invoices, bills, payments, deposits, inventory moves → `JournalEntry` with lines. The engine rejects anything that doesn't balance, and the DB enforces it too (check + trigger / deferred constraint).
2. **Accrual is stored, cash basis is derived** at report time from payment application data.
3. **Void, never delete posted data**, and write an append-only audit record for every change. The PDF lists these as V1 but they're pulled into M0 below; adding them after 50 screens exist means touching all 50.
4. **Closed periods are enforced in the engine**, not the UI.
5. **Provider-neutral persistence** so the Postgres switch in V1 is a config change plus a migration set, not a rewrite.

---

## Phase 1 — MVP (first usable release)

| # | Milestone | Contents | Size |
|---|---|---|---|
| M0 | Foundation | Solution scaffold, `Money`, ledger posting engine + balancing invariant (domain + DB), EF Core/SQLite, schema migrations, void/audit skeleton, local single-user auth, setup wizard (company, fiscal year, business type), backup & restore | L |
| M1 | Ledger core | Chart of accounts (all types, hierarchy, optional numbering), manual multi-line journal entries, fiscal year settings, cash/accrual toggle, CSV import for CoA | M |
| M2 | Banking | Bank & credit card accounts, checks/deposits/transfers, CSV transaction import, statement-based reconciliation + reconciliation report | M–L |
| M3 | Sales / A/R | Customers, products & services, invoices, receive payments (partial, multi-invoice), sales receipts, CSV import for customers & items | L |
| M4 | Purchases / A/P | Vendors, expenses, bills, pay bills (partial, multi-bill), CSV import for vendors | M |
| M5 | Reports + polish | P&L, Balance Sheet, Trial Balance, GL detail, transaction list/journal report, date range filters, global search | M |

**M0 status (2026-10-22): complete.** Scaffold, `Money`, posting engine, void/reversal, audit log, EF Core/SQLite with migrations, DB integrity triggers, minimal REST API, setup wizard, local sign-in (lockout, session revocation on password change), backup download and validated restore with safety copy. Password recovery via the `reset-password` command. 92 tests.

**M1 status (2026-10-22): complete.** QuickBooks account types, chart hierarchy (5 levels) with optional numbering, add/edit/deactivate, CSV import with preview and a starter chart, journal list/entry/detail/void screens, company settings (fiscal year, numbering, default basis), accrual/cash report basis with per-entry basis, trial balance page, API for all of it. 147 tests.

**M2 status (2026-10-22): complete.** Banking overview and per-account register with running balance and C/R marks; checks/payments, deposits, card charges/credits with split lines (negative lines for discounts) and check-number suggestion; transfers (incl. paying a card); void; statement CSV import with column/date/sign detection, per-row categories and duplicate flagging; statement reconciliation (resumable, any entry on the account, void protection) with a printable report that ties register balance = statement + uncleared. API for all of it. 204 tests.

**M3 status (2026-10-22): complete.** Customers (contact, billing/shipping, terms, balances) and products & services with income accounts; invoices with item lines, note lines, 4-decimal prices, due dates from terms, printable view; receive payment across several invoices with oldest-first auto-apply and partial payments; sales receipts with optional customer; void for all three (invoice blocked while paid); A/R auto-created when missing; CSV import for customers and items with preview; sales hub with unpaid/overdue totals; API for all of it. 231 tests.

**M4 status (2026-10-22): complete.** Vendors (terms, account number, default category, balances); bills with category lines, vendor bill numbers and due dates from terms; Pay bills across vendors with partial payments, one numbered check per vendor, from bank or card; expenses as vendor-linked spend transactions; void for bills (blocked while paid) and bill payments (blocked once reconciled); A/P auto-created; vendor CSV import; purchases hub with unpaid/overdue/due-soon totals; API for all of it. 251 tests. **MVP feature set is now complete except M5 reports.**

**M5 status (2026-10-22): complete — the MVP is feature-complete.** Profit and loss, balance sheet (computed retained earnings and current-year net income), trial balance, general ledger detail with drill-down from every statement, journal report, transaction list; period presets that follow the fiscal year; accrual or cash basis everywhere, with cash basis recognizing invoices and bills when paid (allocated across their lines); global search by name, number, reference, memo or amount; API for all of it. 272 tests.

**Exit criteria:** a sole proprietor can set up a company, run a year of invoicing, bills and banking, reconcile, and get a correct P&L and balance sheet, all on one SQLite file they can back up.

## Phase 2 — V1 (what most small businesses expect)

| # | Milestone | Contents | Size |
|---|---|---|---|
| V1.1 | Ledger hardening | Full immutable audit log UI, period close with password override, year-end close (retained earnings), recurring & reversing JEs, opening balances wizard, CoA templates (sole prop, LLC, S-corp, nonprofit) | M–L |
| V1.2 | Banking pro | OFX/QFX/QBO import, bank feed review queue (match/categorize/exclude), bank rules, duplicate detection, undo reconciliation, check printing | L |
| V1.3 | A/R complete | Estimates → invoice, credit memos & refunds, payment terms, recurring invoices, PDF invoice templates, SMTP email, customer statements, undeposited funds, discounts, A/R aging | L |
| V1.4 | A/P complete | Vendor credits, recurring bills, attachments on any transaction, 1099 flagging, A/P aging | M |
| V1.5 | Sales tax | Rates, codes, combined groups, taxable items/customers, exemption certificates, liability report, tax payments | M–L |
| V1.6 | Inventory | Inventory / non-inventory / service items, FIFO cost layers, valuation report, quantity adjustments, reorder points | L |
| V1.7 | Reports, budgets, tax | Cash flow statement, comparative reports, drill-down, export PDF/CSV/XLSX, saved reports, sales/expense/open-items/1099 reports, budgets + budget vs. actual, tax-line mapping + tax summary | L |
| V1.8 | Multi-user & server | PostgreSQL provider, roles (admin/standard/view-only/accountant), encryption at rest (SQLCipher for SQLite), Docker image, full REST API, webhooks | L |
| V1.9 | Portability & UX | Transaction + opening-balance CSV import, full documented data export, QuickBooks IIF import, keyboard quick-entry, register view, notes on any record, dark mode | M |

Suggested order: V1.1 → V1.3 → V1.4 → V1.2 → V1.5 → V1.7 → V1.6 → V1.8 → V1.9.
Sales tax must land before inventory, since both change how invoice lines post. Multi-user goes late so the API surface is stable before it's exposed.

**V1.1 status (2026-10-22): complete.** Closing date with an optional closing password: changes dated in a closed period (posting, voiding, any document) are refused or need the password, and each override is audited; reopening or changing the password needs the current one. Year-end close moves the closing date to the fiscal year end (no closing entries; prior years' income shows on a "Retained Earnings" account when the chart has one). Reversing entries ("reverse on" date). Recurring journal entries (weekly to yearly, end date or count, month-end safe, catch-up, manual or automatic posting by an hourly background job as "system"). Opening balances wizard (difference to Opening Balance Equity). Starter charts for sole proprietor/LLC, partnership, corporation and nonprofit, offered on the empty chart of accounts after setup. Audit log page with filters, per-record history and field-level changes. API for all of it. 303 tests.

**V1.3 status (2026-10-23): complete.** Estimates (edit until invoiced, accept/decline, expiry) that become invoices; discount lines (percent of the items above, or an amount) posting to Discounts Given; payment terms with early payment discounts ("2% 10, Net 30") taken at Receive payment; payments larger than what's applied leave customer credit; credit memos; applying credit to invoices (no posting); refunds of credit memos and unapplied payments; Undeposited Funds and Make deposit; recurring invoices created on a schedule, manually or by the hourly job, optionally emailed; customer statements (balance forward, running balance, aging); A/R aging summary and detail as of any date; PDF invoices, estimates, credit memos, receipts and statements (MigraDoc) in Classic, Modern or Compact style with accent color and footer; SMTP email through MailKit (or save to a folder) with the PDF attached and every send audited. Cash basis covers discounts, unapplied payments and credit memo refunds. API for all of it. 341 tests.

## Phase 3 — LATER (pick by audience)

| # | Module | Notes |
|---|---|---|
| L1 | Segments | Classes, departments, tags, locations; budgets by segment |
| L2 | Multi-currency & multi-entity | FX rates, realized/unrealized gain/loss, consolidation, intercompany. **Decide early whether you'll ever want this**: if yes, put a currency column on every amount in M0 even if it's always the home currency |
| L3 | A/R & A/P advanced | Late fees, reminders, Stripe/Square/PayPal links, customer portal, progress invoicing, sales orders, retainers, bad debt, sub-customers, POs with 3-way match, approvals, NACHA, billable expenses, receipt OCR |
| L4 | Banking & inventory advanced | Plaid/SimpleFIN/GoCardless feeds, positive pay, petty cash; weighted average, kits, assemblies, warehouses/bins, lot/serial, barcodes, landed cost |
| L5 | Projects & assets | Time tracking, timer, billable time, project budgets & profitability, mileage; fixed asset register, depreciation (SL, DB, MACRS), disposal, loan amortization |
| L6 | Reporting advanced | Custom report builder, scheduled emails, dashboards, KPIs, cash-flow forecasting, scenarios; 1099 e-file, accountant package & adjusting mode |
| L7 | Security | Granular permissions, OIDC/SSO, 2FA, accountant invite |
| L8 | Migration | QBO API / QBXML, GnuCash, Xero/Wave/FreshBooks, scheduled cloud backups |
| L9 | Platform | Plugin system, PWA, offline sync, CLI, localization |
| L10 | Payroll | Recommend the **"payroll journal import"** option first (import totals from Gusto/ADP). Full payroll needs yearly tax-table maintenance indefinitely |
| L11 | Industry editions | Nonprofit, contractor, retail, professional services, manufacturing, e-commerce, property management; each is a mini-product |

---

## Token usage estimate (building with Claude Code)

### How to read these numbers

In an agentic coding session, every tool call re-sends the conversation so far. Most tokens processed are therefore **cached input re-reads** (typically 85–95%), which are billed and rate-limited far more cheaply than fresh input or output. Figures below are:

- **Total**: all tokens processed (cache reads + fresh input + output)
- **Output**: tokens Claude writes (code, tests, explanations)

Per-feature assumptions, including tests, a build/test loop and normal debugging:

| Size | Example | Total | Output |
|---|---|---|---|
| S | CRUD list/screen, simple report | 0.5–1 M | ~10 k |
| M | Workflow with posting rules (invoice, pay bills) | 2–4 M | ~30 k |
| L | Reconciliation, FIFO, sales tax engine, Postgres + Docker | 5–10 M | ~80 k |

### By milestone

| Phase | Milestone | Total tokens | Output tokens |
|---|---|---|---|
| MVP | M0 Foundation | 12–20 M | ~150 k |
| | M1 Ledger core | 8–14 M | ~90 k |
| | M2 Banking | 10–16 M | ~100 k |
| | M3 Sales / A/R | 12–20 M | ~130 k |
| | M4 Purchases / A/P | 8–12 M | ~80 k |
| | M5 Reports + polish | 10–16 M | ~110 k |
| | **MVP subtotal (+20% rework buffer)** | **~70–120 M** | **~0.8 M** |
| V1 | V1.1 Ledger hardening | 15–25 M | ~170 k |
| | V1.2 Banking pro | 15–25 M | ~170 k |
| | V1.3 A/R complete | 20–35 M | ~250 k |
| | V1.4 A/P complete | 8–14 M | ~90 k |
| | V1.5 Sales tax | 12–20 M | ~140 k |
| | V1.6 Inventory | 15–25 M | ~170 k |
| | V1.7 Reports, budgets, tax | 20–35 M | ~250 k |
| | V1.8 Multi-user & server | 20–35 M | ~250 k |
| | V1.9 Portability & UX | 12–20 M | ~140 k |
| | **V1 subtotal (+20%)** | **~165–280 M** | **~1.9 M** |
| LATER | L1 Segments | 10–20 M | |
| | L2 Multi-currency / entity | 40–80 M | |
| | L3 A/R & A/P advanced | 40–70 M | |
| | L4 Banking & inventory advanced | 25–45 M | |
| | L5 Projects & assets | 30–50 M | |
| | L6 Reporting advanced | 20–35 M | |
| | L7 Security | 10–20 M | |
| | L8 Migration | 25–45 M | |
| | L9 Platform | 30–60 M | |
| | L10 Payroll (full) | 80–150 M | |
| | L11 Industry editions | 60–150 M | |
| | **LATER subtotal** | **~370–725 M** | **~4–7 M** |

**Through V1: roughly 235–400 M total tokens, ~2.7 M output.**
Everything in the PDF: roughly 0.6–1.1 B total, ~7–10 M output.

### Confidence and calibration

These are order-of-magnitude estimates (±50%). The biggest swing factors are how long sessions run before `/clear`, how much debugging a feature needs, and file sizes. **Calibrate after M0:** check what the foundation actually used, divide by the M0 estimate, and scale the rest of the table by that ratio.

### Keeping usage down

- One feature per session; `/clear` between features. Context size multiplies every call.
- Keep a short `CLAUDE.md` with the design rules above so they don't have to be rediscovered each session.
- Keep files small (one entity / one endpoint group / one component per file) so reads are cheap.
- Use a cheaper model (Sonnet / Haiku) for CRUD screens, CSV importers and report formatting; save Opus for the ledger engine, reconciliation, FIFO, sales tax and the Postgres migration.
- Let `dotnet test` with filtered output do the verification, not repeated file re-reads.
