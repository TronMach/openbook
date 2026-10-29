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

**V1.4 status (2026-10-23): complete.** Vendor credits (the mirror of a bill) applied to bills without posting, with refunds received from the vendor; recurring bills entered manually or by the hourly job; attachments on any document, customer or vendor (stored in the company file so backups include them; PDFs, images, text, CSV and Office files up to 10 MB; risky types refused; served with nosniff and a sandboxing content policy); vendors tracked for 1099s with a masked tax ID and form box; 1099 summary by year ($600 through 2025, $2,000 from 2026, editable for later years; card payments left out as 1099-K); A/P aging summary and detail as of any date. Cash basis covers vendor refunds. API for all of it. 363 tests.

**V1.2 status (2026-10-23): complete.** OFX, QFX and QBO statement import (SGML and XML) alongside CSV; imported lines go to a review queue per account where each is added as a new transaction, matched to one already recorded (same amount within 7 days), or excluded, and any decision can be undone (an added line's transaction is voided). Duplicate detection by the bank's transaction id, or by date, amount, payee and reference for CSV, so re-importing a file adds nothing. Bank rules (text in payee or memo, direction, amount range, account) suggest a category, payee and vendor, or add lines automatically. Undo the latest reconciliation (reopens it with its ticks). Check printing on voucher stock for expenses and bill payments, with the amount in words and printed status. API for all of it. 391 tests.

**V1.5 status (2026-10-23): complete.** Tax agencies (each with its own liability account under Sales Tax Payable, a filing frequency and permit number), rates (percentages to 4 decimals, paid to one agency) and tax codes that charge one rate or combine several; taxable flags on items with per-line overrides; a default tax code and exemption certificate (reason, number, expiry) on each customer. Invoices, sales receipts, credit memos and estimates add one tax line per rate: the combined tax is rounded once and split by rate, discounts reduce the taxable amount by their taxable share, and an exempt customer is charged zero (and reported as exempt) until the certificate expires. Live tax preview on the sales forms; tax shows as totals on screen and on PDFs. Sales tax liability report by agency and rate (total, non-taxable, exempt and taxable sales, tax, owed at start and end, paid). Pay sales tax from a bank or card with an optional discount or penalty adjustment; payments are voidable and take attachments. Cash basis recognizes tax as invoices are paid. API for all of it. 413 tests.

**V1.7 status (2026-10-23): complete.** Statement of cash flows (indirect method); comparative profit and loss (previous period, same period last year, by month or quarter with totals, $ and % change) and balance sheet (a month or year earlier); drill-down from every amount to the general ledger for that column's dates and from names to customers, vendors, items and documents; export of every report to CSV, Excel (ClosedXML) and PDF, all rendered from the same table as the screen; saved reports with their settings (relative periods stay relative); sales by customer and expenses by vendor (tied to the P&L on either basis), sales by product or service, open invoices, unpaid bills, 1099 detail; budgets by month for a fiscal year (blank, from last year's actuals or copied; spread a yearly figure) with budget vs. actual; tax line mapping for Schedule C, 1065, 1120-S, 1120 and 990 with a tax summary. API for all of it. 455 tests.

**V1.6 status (2026-10-24): complete.** Inventory products alongside services and non-inventory products, each with an inventory asset account, cost of goods sold account, purchase cost and reorder point. Bills receive stock on item lines; invoices and sales receipts take it out first in, first out, posting cost of goods sold in the same entry; credit memos put returns back at the latest sale's cost; stock can't be sold before it's on hand. Quantity adjustments (counts, shrinkage, starting stock against Opening Balance Equity) with attachments. Voids put stock back and are refused once later sales used it. Item pages show stock on hand, value and history; an inventory page lists stock with reorder flags; inventory valuation (as of any date, summary or by cost layer, tied to the books) and stock status reports. Cash basis recognizes cost of goods sold at the sale. API for all of it. 476 tests.

**V1.8 status (2026-10-24): complete.** Roles: view-only, standard, accountant and admin, enforced for every page, form and API call from one rule table, with a users page to add people, change roles, reset passwords and deactivate (always one active admin; role changes sign the person out). API keys per user for the REST API (bearer tokens, shown once, revocable, optional expiry) and an OpenAPI description of every endpoint. Webhooks for any audited change (filter by event pattern), signed with HMAC-SHA256, retried with backoff, with a delivery log, test ping and redelivery. Encryption at rest with SQLCipher (key from configuration; backups stay encrypted; commands to encrypt or decrypt an existing file). PostgreSQL provider with its own migrations and the ledger triggers in PL/pgSQL. Dockerfile and docker-compose with PostgreSQL, health probe. 526 tests; the whole integration suite also passes on PostgreSQL 17 (312 tests, SQLite-only backup tests skipped). The Docker image (443 MB) and the docker-compose stack with PostgreSQL 17 were built and run: setup, posting, PDF invoices and the ledger triggers all work in the containers.

**V1.9 status (2026-10-24): complete — Phase 2 (V1) is feature-complete.** Journal entry and opening balance CSV import with preview (accounts by name or number, debit/credit or signed amounts, entries grouped by number or by balancing, date order detected). QuickBooks Desktop IIF import of accounts, customers, vendors, products and services, and transactions (as journal entries), all-or-nothing in one transaction. Full data export: a ZIP of every table as CSV, every attachment, and a generated README documenting each file and column, with secrets left out. Notes on any record, audited. A register for any account with a keyboard quick-entry row. Keyboard shortcuts (search, go to, new, Ctrl+Enter to save, help with ?). Dark mode as a choice: follow the system, light or dark. API for imports, export and notes. 546 tests; the integration suite also passes on PostgreSQL.

**L12 status (2026-10-24): core complete (items 1–10).** Price levels per customer (fixed percentage or per item prices, with CSV import and export) as the baseline; price rules for promotions and exceptions (items, customers or levels, date and quantity ranges, percent, amount or set price, priority, exclusive, active); quantity breaks per item counted across the whole document; rounding (none, cent, 5¢, 10¢, dollar, .99) as a company default with per rule overrides; one documented order of application in a single calculator used by the server and the live form preview; list price and pricing source kept on every line, with "you saved" on screens and PDFs; the last price charged shown on sales forms; manual prices always kept; audit, webhooks and a REST API; price list and discounts-given reports. 578 tests; the integration suite also passes on PostgreSQL. Not yet: price levels on purchases, customer-specific item catalogs, rule usage limits.

**L1 status (2026-10-25): complete.** Classes on every line and a location (renameable: department, division, store...) on every transaction, both nested up to five levels, plus tags on transactions. Kept beside the ledger, so posted entries stay immutable while lines can be reclassified in bulk afterwards (documents follow, so cash basis agrees). Invoices, receipts, credit memos, estimates, bills, vendor credits, bank spends and receipts, and journal entries carry them; voids copy them. Profit and loss by class and by location; class, location and tag filters on the P&L, general ledger, sales by customer and expenses by vendor, on accrual and cash basis; budgets per class or location. Tracking toggles, API and audit. 598 tests; the integration suite also passes on PostgreSQL. Not yet: segments on recurring templates and imports, required-class enforcement.

**L8 status (2026-10-25): complete.** One "move in" page and API for QuickBooks Desktop (IIF and qbXML SDK responses), QuickBooks Online (API query JSON), GnuCash (XML books, compressed or not), Xero (chart, contacts and manual journal CSVs), Wave (accounting transactions CSV) and FreshBooks (clients CSV), all through one previewed, all-or-nothing import pipeline. Scheduled backups, daily or weekly, to a folder or S3-compatible storage (Amazon S3, Backblaze B2, Cloudflare R2, Wasabi, MinIO) with SigV4 signing and retention, run in the background with the last outcome shown. 616 tests; the integration suite also passes on PostgreSQL. Not done: a live QuickBooks Online OAuth connection (needs an Intuit developer app), invoices and bills as documents rather than journal entries, and scheduled backups for PostgreSQL (use pg_dump).

**L4 plan (2026-10-26):** built in three slices. (1) Banking: live bank feeds through SimpleFIN Bridge, positive pay issue files, petty cash. (2) Inventory costing: weighted average per item, kits, assemblies (builds), landed cost. (3) Stock tracking: warehouses and bins, lot and serial numbers, barcodes. Plaid and GoCardless go behind the same connection interface once there are developer credentials to build and test against.

**L4 slice 1 status (2026-10-26): complete.** Bank connections through SimpleFIN Bridge: paste a setup token, choose which bank or card account each remote account fills and from what date, and posted transactions arrive in the bank feed every 6 hours (or on "Sync now") for review, deduplicated by the bank's transaction id; problems the service reports are shown on the connection. Positive pay issue files (CSV) of numbered checks and voids per bank account, remembering the bank's account number, date format and how far exports have gone. Petty cash boxes on the imprest system: fund from the bank, count the cash (with notes and coins), over/short posted automatically, replenish back to the float. API for all of it. Not done: Plaid and GoCardless (need developer accounts), fixed-width positive pay layouts.

**L4 slice 2 status (2026-10-26): complete.** Weighted average costing per item alongside FIFO. Bundles (kits) that sell as one line at their own price and take their inventory components out of stock. Assemblies with a bill of materials and assembly builds (stock check and "how many can I build" before building; void to unbuild). Landed costs: spread freight, duty and similar costs over the stock on chosen bills by value or quantity, with the share of stock already gone charged to cost of goods sold. Inventory page lists builds and landed costs; item pages edit components and show build and landed cost history. API for all of it. Not done: landed cost shares for stock that went into assemblies are expensed rather than carried into the assemblies; pending builds and partial builds; bundles' own stock.

**L4 slice 3 status (2026-10-26): complete, so L4 is done.** Warehouses (a default "Main" plus any others) and bins inside them; bills, invoices, sales receipts and credit memos name the warehouse their stock moves in or out of, adjustments name a warehouse and bin, and transfers move stock between them without touching its cost. Lot and serial number tracking per item: receipts name lots or serials (each its own cost layer), sales name them or take the oldest, and the numbers used are kept on the document lines and printed on forms; a lookup traces any lot or serial. Barcodes on items, found by scanning into any item picker. Stock by warehouse, bin and lot on item pages and the warehouse page. API for all of it. Not done: lot expiry dates, barcode label printing, per-warehouse costing, warehouse filters on reports.

**L4 loose ends (2026-10-26): done.** Landed costs on stock that went into assemblies now go onto the assemblies (and their cost of goods sold for assemblies already sold). Lot expiry dates, with the soonest-expiring lot picked first and expired lots skipped unless named, and a list of lots expiring in the next 30 days. Barcode label sheets (Code 128, 30 per Letter page). A warehouse filter on the inventory valuation and stock status reports. Still open: Plaid and GoCardless feeds (need developer accounts), fixed-width positive pay layouts, partial builds, bundles with their own stock.

**L3 plan (2026-10-29):** built in four slices. (1) Collections: sub-customers (optionally billed with their parent), bad debt write-offs, late fees, automatic payment reminders. (2) Sales flow: sales orders, progress invoicing, retainers (deposits held as a liability), billable expenses. (3) Purchasing controls: purchase orders with receiving and 3-way match, approvals, NACHA payment files. (4) Getting paid online: Stripe/Square/PayPal payment links, customer portal, receipt OCR.

**L3 slice 1 status (2026-10-29): complete.** Sub-customers (jobs, projects, locations) up to five levels, shown as a tree; a sub-customer can be billed with its parent, so the parent's payments and credits settle its invoices and it appears on the parent's statement and reminders. Bad debt write-offs of one or many overdue invoices to "Bad Debts" or another expense account, voidable if the customer pays after all, left out of cash-basis reports. Late fees (percentage and/or flat amount, grace period, once or repeating) charged as separate fee invoices, by hand from the collections page or automatically every hour. Payment reminders emailed with the invoice on a schedule around the due date, by hand or automatically, each step once. API for all of it. Not done: sales by customer subtotals for parents, a sales tax bad debt deduction, per-customer reminder schedules.

**L3 slice 2 status (2026-10-29): complete.** Sales orders (customer PO number, expected date, warehouse) invoiced in full or in parts by quantity, closing when everything is billed and reopening if an invoice is voided; the rest of an order can be cancelled. Progress invoicing of estimates by a percentage of every line or by quantities, with what's been invoiced shown on the estimate. Retainers and customer deposits held in a "Customer Deposits" liability, applied to invoices (recognized as income on cash basis when applied) or refunded, each step voidable. Billable expenses: bill and expense lines marked billable to a customer with a markup, waiting on a list until ticked onto that customer's invoice. API for all of it. Not done: marking lines billable while entering a bill or expense (it's done from the posted document), retainers into undeposited funds, stock reservations for sales orders, progress invoicing by amount rather than quantity.

**L3 slice 3 status (2026-10-29): complete.** Purchase orders for inventory items and categories, goods received against them (and taken back), and the vendor's bill entered from the order, in parts if need be, closing the order when it's all billed. 3-way match: bills that bill more than was ordered or received, or cost more than the order beyond a tolerance, are held for approval with the reasons listed. Bill approvals above a set limit, approved or rejected by someone other than the person who entered the bill (unless allowed), and held bills can't be paid. ACH payments: vendors' bank details (encrypted, shown as the last four digits), the bank's ACH details per bank account, and NACHA files (CCD credits) of chosen bill payments, each payment sent once, kept for download again. API for all of it. Not done: purchase order PDFs and emailing, stock received before the bill (item receipts posting to an accrual), approvals for purchase orders and expenses, ACH debits and prenotes, positive pay for ACH.

**Order (decided 2026-10-26):** L13 Desktop and Server editions and L7 Security come last, once the features have settled; they package and harden what the other modules build.

## Phase 3 — LATER (pick by audience)

| # | Module | Notes |
|---|---|---|
| L1 | Segments | Classes, departments, tags, locations; budgets by segment |
| L2 | Multi-currency & multi-entity — **on hold indefinitely (decided 2026-10-25)** | Not planned; don’t start without a new decision. Journal entries record a currency (always the home currency), but documents, lines and reports assume one currency, so reviving it touches most modules. Scope if revived: FX rates, realized/unrealized gain/loss, consolidation, intercompany. **Decide early whether you'll ever want this**: if yes, put a currency column on every amount in M0 even if it's always the home currency |
| L3 | A/R & A/P advanced | Late fees, reminders, Stripe/Square/PayPal links, customer portal, progress invoicing, sales orders, retainers, bad debt, sub-customers, POs with 3-way match, approvals, NACHA, billable expenses, receipt OCR |
| L4 | Banking & inventory advanced | Plaid/SimpleFIN/GoCardless feeds, positive pay, petty cash; weighted average, kits, assemblies, warehouses/bins, lot/serial, barcodes, landed cost |
| L5 | Projects & assets | Time tracking, timer, billable time, project budgets & profitability, mileage; fixed asset register, depreciation (SL, DB, MACRS), disposal, loan amortization |
| L6 | Reporting advanced | Custom report builder, scheduled emails, dashboards, KPIs, cash-flow forecasting, scenarios; 1099 e-file, accountant package & adjusting mode |
| L7 | Security | Granular permissions, OIDC/SSO, 2FA, accountant invite |
| L8 | Migration | QBO API / QBXML, GnuCash, Xero/Wave/FreshBooks, scheduled cloud backups |
| L9 | Platform | Plugin system, PWA, offline sync, CLI, localization |
| L10 | Payroll — **on hold indefinitely (decided 2026-10-25)** | Not planned; don’t start without a new decision. If revived: recommend the **"payroll journal import"** option first (import totals from Gusto/ADP). Full payroll needs yearly tax-table maintenance indefinitely |
| L11 | Industry editions | Nonprofit, contractor, retail, professional services, manufacturing, e-commerce, property management; each is a mini-product |
| L12 | Advanced pricing | Price levels per customer (fixed percentage or per-item prices) as the baseline; price rules for promotions and exceptions (items, customers or levels, quantity range, date range, percent/amount/set price, priority, exclusive); quantity breaks per item; rounding; a defined order of application; list price and pricing source kept on every line; last price sold; price list and discounts-given reports. Researched from QuickBooks Enterprise Advanced Pricing, fixing its gaps (undocumented precedence, quantity breaks ignoring split lines) |
| L13 | Desktop and Server editions | One codebase, two installs. **Desktop**: self-contained Windows/macOS installer, SQLite in the user's Documents, listens on localhost only, scheduled folder backups on by default, no database or port settings shown. **Server**: the Docker image and compose stack with PostgreSQL, admin docs for HTTPS behind a reverse proxy, Data Protection keys and pg_dump backups. **Upgrade path**: a command that copies a SQLite company file into PostgreSQL (ledger protections kept, balance and audit checked afterwards) so a business can move from Desktop to Server. Screens adapt to the edition (e.g. pg_dump guidance instead of SQLite backup buttons) |

### Backlog (from L4)

| Item | Why it waits |
|---|---|
| Plaid and GoCardless bank feeds | They need developer accounts. The bank connection interface already exists (SimpleFIN uses it), so each becomes another provider behind it. |
| Fixed-width positive pay | Each bank uses its own layout. Today's positive pay file is CSV. |
| Partial and pending builds (decided and built 2026-10-26) | A build that's short can be saved as **pending** (posts nothing, touches no stock; a pending list shows what's short) and finished later with "Build now", posting on that date. When short, the build page also offers to **build what you can** now and leave the rest pending. Components are **not** reserved. |
| Kits with their own stock (decided and built 2026-10-26) | Bundles stay stockless; pre-packed kits are **assemblies**, and the item page says so. New per-assembly option **auto-build on sale**: a sale short of finished units builds the shortfall from components in the same save. Bundles don't get their own stock. |

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
| | L2 Multi-currency / entity, on hold, not in subtotal | (40–80 M) | |
| | L3 A/R & A/P advanced | 40–70 M | |
| | L4 Banking & inventory advanced | 25–45 M | |
| | L5 Projects & assets | 30–50 M | |
| | L6 Reporting advanced | 20–35 M | |
| | L7 Security | 10–20 M | |
| | L8 Migration | 25–45 M | |
| | L9 Platform | 30–60 M | |
| | L10 Payroll (full), on hold, not in subtotal | (80–150 M) | |
| | L11 Industry editions | 60–150 M | |
| | L13 Desktop and Server editions | 15–30 M | |
| | **LATER subtotal (without payroll and multi-currency)** | **~265–525 M** | **~4–7 M** |

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
