# OpenBook

Open-source QuickBooks alternative in .NET 10. Plan and milestones: `ROADMAP.md`.

## Commands

- Build: `dotnet build`
- Test: `dotnet test` (Domain.Tests = pure unit; Integration.Tests = migrated in-memory SQLite incl. triggers)
- Run: `dotnet run --project src/OpenBook.Web` (migrates `openbook.db` on startup; API under `/api`)
- Reset a forgotten password: `dotnet run --project src/OpenBook.Web -- reset-password [username]` (same config as the web app; prompts twice, reads lines when stdin is piped; refuses if the company file is missing rather than creating one)
- New migration: `dotnet ef migrations add <Name> -p src/OpenBook.Infrastructure -s src/OpenBook.Infrastructure -o Persistence/Migrations`

## Layout

Domain (no EF/ASP.NET) → Application (use cases, `ILedgerStore`) → Infrastructure (EF Core) → Api (minimal API endpoints, class library) → Web (Blazor host, maps the API).

## Ledger rules (do not bypass)

- Everything that affects balances posts through `PostingEngine` as a `JournalEntry`. No other code writes `JournalLines`.
- Money is `decimal` (or `Money`) in code and integer minor units (`AmountMinor`) in the DB. Never `double`/`float` in Domain (`ArchitectureTests` enforces).
- Posted entries are immutable. Corrections = `VoidAsync`, which posts a same-date reversal. Never delete.
- Every change writes an `AuditRecord` in the same save.
- DB triggers enforce balance-on-seal, immutability and append-only audit (overview in `SqliteLedgerTriggers`). New entries are inserted unsealed and sealed in the same transaction by `OpenBookDbContext.SaveChangesAsync`.

## Migrations (SQLite)

- Trigger SQL lives frozen inside the migration that creates it (`*_InitialLedger.Triggers.cs`, `*_ChartAndSettings.cs`). Never edit an applied migration; to change a trigger, add a migration that drops and recreates it. Add new trigger names to `SqliteLedgerTriggers.Names`.
- A new column on `JournalEntries` must be added to the `TR_JournalEntries_Immutable` condition (via a new migration).
- Never let EF rebuild `JournalEntries`, `JournalLines` or `AuditRecords` (its SQLite `DropColumn`/`AlterColumn`): the rebuild loses triggers and its table swap runs after all `Sql()` steps. Drop the triggers, use native `ALTER TABLE … DROP COLUMN` via `Sql()`, recreate the triggers. Check generated migrations for `ef_temp_`.
- Backfill non-null columns explicitly in the migration (`defaultValue`), not with model `HasDefaultValue` (EF's sentinel rule would turn `false`/`0` into the DB default).
- `MigrationTests` cover a fresh database, upgrading an M0-era file, and downgrading.
- Keep SQL provider-neutral (quoted identifiers, no SQLite-only functions) for the V1.8 PostgreSQL provider.
- Domain errors throw `DomainException` with a stable `code`; the API maps them to 400 (404 for `EntityNotFoundException`).

## Company, sign-in, backup

- One company per database, created by `/setup` (`CompanySetupService`). Home currency comes from the `Company` row; ledger services throw `company.not_set_up` before setup. `SetupGateMiddleware` routes pages to `/setup` until then.
- Cookie auth (`Web/Security/WebSecurity.cs`). The cookie carries the user's `SecurityStamp`, checked on every request, so changing a password or restoring a backup ends other sessions. Pages require auth via `Components/Pages/_Imports.razor`; mark public pages `[AllowAnonymous]`. The `/api` group requires auth and returns 401 instead of redirecting.
- Audit actor comes from `ICurrentUser`, which the host registers (`WebCurrentUser` covers both HTTP requests and interactive circuits).
- Backup = SQLite online backup API → plain `.openbook.db` file. Restore validates (integrity, known migrations, triggers present, company present, ledger balanced), writes a safety copy to `backups/` next to the company file, then copies over the live DB in place. A new trigger must be added to `SqliteLedgerTriggers.Names` or restore won't check for it.
- Pages are static server-rendered by default; use plain HTML forms with `<AntiforgeryToken />` for posts to minimal API endpoints (`Web/Endpoints`). Wrap a page's content in `<div class="wide">` for the wide layout.
- Interactive pages (`@rendermode InteractiveServer`: journal entry form, CSV import) must not inject application services directly: use `Operations.RunAsync` so each action gets a fresh scope and the right audit actor.

## Chart of accounts and reporting

- `AccountType` is QuickBooks' list (Bank, AccountsReceivable, …); `AccountClass` groups them for statements. Pick the narrowest type; M2–M4 key off Bank/CreditCard/AR/AP.
- Rules spanning accounts live in `ChartOfAccounts` (subaccount = parent's type, max 5 levels, unique sibling names and numbers, no cycles, class change blocked once posted). Full names use `Parent:Child`. Accounts are deactivated, never deleted.
- CSV import (`ChartImportService`, parser in `Application/Import/Csv.cs`) matches headers loosely and is all-or-nothing. The starter chart at `Web/wwwroot/samples/chart-of-accounts.csv` is covered by a test.
- `ReportBasis` (Accrual/Cash) per report, default from company settings. `EntryBasis` on each entry (Both / AccrualOnly / CashOnly) decides inclusion. Cash basis for invoices and bills is payment-driven; see Reporting below.
- Dev preview: `.claude/launch.json` → `openbook-web` on port 5188, data in `src/OpenBook.Web/openbook.dev.db`.

## Banking (M2)

- Bank and credit card accounts are accounts of type `Bank` / `CreditCard`. Amounts in banking code are in the account's natural sign (`BankingRules.Natural`): positive = money in the bank, or owed on a card.
- `BankTransaction` is a document (Spend / Receive / Transfer, with payee and check number) that posts one journal entry with `Source = bank.*` and `SourceId` = the document id. Categories are the entry's lines. Documents are voided, never edited; the journal page refuses to void entries posted by documents.
- Services that create several entries in one save use `PostingEngine.StageAsync` / `StageVoidAsync` (internal) and save once; `NextEntryNumberAsync` counts staged entries. Mutate documents only after the staged call succeeds, so a refusal leaves nothing changed in memory.
- Reconciliation state lives in `Reconciliations` / `ReconciliationLines` referencing ledger lines; the ledger itself is never touched. One in progress per account, ticks saved as made. Voiding is blocked for any entry with a ticked or reconciled line. Voided entries and their reversals are left out of worksheets and reports.
- Statement CSV import: `BankImportService` (headers matched loosely; one signed amount column or money-out/money-in columns) with `StatementValues` for dates and amounts. It refuses to guess on ambiguous amounts ("1.005") and flags ambiguous date orders. Possible duplicates = same date, signed amount and payee as an existing posted transaction.

## Sales (M3)

- `Customer` and `Item` (products and services; `Service` or `NonInventory`, each with a default price and income account) are deactivated, never deleted. Names are unique ignoring case. Customers with unpaid invoices can't be deactivated.
- `Invoice`, `CustomerPayment` and `SalesReceipt` are documents posting one journal entry each (`Source = sales.*`), voided never edited. Lines (`SalesLine`) need an item when they have an amount; item-less lines are notes. Rates and quantities allow 4 decimals (stored as ten-thousandths); line amounts round half away from zero to the cent. `SalesLine.Postings` credits each income account with its lines' sum (debits it if a discount makes it negative).
- Invoices post to the first active A/R account; one is created and saved on first use if none exists. Payments deposit to a Bank or Other current asset account (Undeposited Funds by default when the chart has it). Invoice `AmountPaid` is everything settled (payments, early payment discounts, applied credit) and is kept in step by payment create/void and credit application. An invoice with anything applied can't be voided.
- Document services check everything before `StageAsync` and mutate documents/invoices only after staging succeeds.
- Customer and item CSV imports (`SalesImports.cs`) follow the chart import pattern (preview, all-or-nothing, existing names skipped). `CsvColumns` is the shared header matcher.
- SQLite can't `ORDER BY` a `DateTimeOffset`; order such results in memory.
- `Web/Components/Links.cs` maps a journal entry's source to its document page; use it wherever a ledger line links somewhere.

## Purchases (M4)

- `Vendor` mirrors `Customer` (plus your account number with them and a default expense category that pre-fills bills and expenses). Vendors with unpaid bills can't be deactivated.
- `Bill` lines are category lines (account, description, amount; negative = credit), not items; any active account except A/R and A/P. Bills post Dr categories / Cr the first A/P account (created and saved on first use). Bill numbers are the vendor's, unique per vendor. A bill with payments can't be voided.
- `BillPaymentService.PayAsync` takes any open bills across vendors and writes one `BillPayment` per vendor (and per A/P account) in one save, vendors in name order; numeric check numbers count up from the first. Paid from a Bank or Credit card account. The vendor's account number is the default memo.
- Expenses (paid immediately) are `BankTransaction` spends with an optional `VendorId`; the payee defaults to the vendor's name. Check-number suggestions count both expense and bill-payment references.
- `DocumentStatus` (Posted/Voided) is shared by sales and purchase documents; `Text.Optional` lives in Domain.Common.

## Reporting (M5)

- Every report reads lines from `ReportLedger.LinesAsync(basis, from, to)`, never from the store directly, so all reports agree. Accrual = ledger as posted (entry basis permitting). Cash = entries whose basis allows cash, minus everything sourced from invoices, bills, customer payments and bill payments; plus, for each posted payment in range, derived lines on the payment date: the deposit/payment account for the full amount, and the paid documents' accounts allocated in proportion to their lines (`ReportLedger.Allocate`, rounding cent on the last line). A new accrual document type must be added to `AccrualDocuments` and given a derivation.
- `FinancialReports`: P&L sections by account type; balance sheet with "Retained earnings" (net income before the fiscal year containing the as-of date; merged into a top-level, childless Equity account named "Retained Earnings" if there is one, else a computed row) and computed "Net income" (since its start); general ledger with opening and running balances in each account's natural sign. Rows link to the general ledger for drill-down.
- `TrialBalanceQuery` also runs on `ReportLedger`. `DateRanges` has the period presets (calendar month/quarter, fiscal year).
- `SearchService` searches in memory across names, numbers, references, memos and amounts (`StatementValues.TryParseAmount`), max 20 hits per kind.
- Report pages share `Components/Reports` (`ReportFilter`, `ReportQuery`, `ReportSectionRows`); they're static GET forms so every report has a shareable, printable URL.

## Ledger hardening (V1.1)

- Closing date and password live on `Company`. `PostingEngine` checks every staged entry and void against it (`EnsureOpen`): `period.closed` without a password, `period.password_required` / `period.wrong_password` with one. The password comes from the scoped `ClosedPeriodOverride` (API: `X-Closing-Password` header; interactive pages: `Operations.RunAsync(..., closingPassword)`; static forms: set it before calling the service). Each override writes an `AuditAction.ClosedPeriodOverride` record on the company. Show `ClosedPeriodPassword` on any form that posts, once `ClosedPeriod.NeedsPassword(ex)`.
- `CloseBooksService`: moving the closing date earlier, clearing it, or changing/removing the password needs the current closing password; closing more doesn't. Year-end close only moves the closing date; nothing is posted.
- Journal-sourced entries (`JournalEntry.IsJournalSource`: manual, auto-reversal, recurring, opening balance) are voided from the journal page; anything else from its document.
- `ReverseOn` posts a mirror entry (`AutoReversalSource`, `SourceId` = original). Recurring templates (`RecurringEntry`) compute each date from `StartDate` and the occurrence index, so month ends don't drift; `RecurringEntryService.PostDueAsync` saves each occurrence separately and stops a template at its first error. `RecurringPostingService` (Web) runs it hourly for auto-post templates as actor "system".
- `JournalEntryFilter` can filter by `Source`/`SourceId`; use it rather than scanning entries.
- Opening balances post against "Opening Balance Equity" (created on first use); A/R and A/P are excluded (enter open invoices/bills instead).
- Starter charts are embedded CSVs in `Application/Accounts/Templates`, imported through `ChartImportService`; each has a top-level "Retained Earnings" equity account.
- `AuditLogQuery` filters in memory and diffs top-level JSON fields (null = absent). `Links.ForAuditEntity` maps an entity type to its page; add new entity types there.
- `JournalLinesEditor` (Components/Journal) is the shared debit/credit grid; the parent owns the `JournalLineModel` list and checks it with `JournalLines`.

## Receivables (V1.3)

- Sales lines have a `SalesLineKind`: Item, Note, DiscountPercent (percent of the item lines above it) or DiscountAmount. Discount lines post to the income account "Discounts Given" (`SalesAccounts.GetOrCreateDiscountsAsync`). Build lines with `SalesLineBuilder`, never `SalesLine.FromDrafts` directly in services.
- Terms are `PaymentTerms` (due days plus optional early payment discount) from the customer, copied onto each invoice (`EarlyPaymentDiscountPercent`/`Date`). A payment application has `Amount` (money) and `Discount` (posted Dr Discounts Given); `Settled` is both.
- A `CustomerPayment` has an explicit `Amount`; what isn't applied is customer credit (`Unapplied`). Credit (credit memo balance or unapplied payment) is applied to invoices by `CreditService.ApplyAsync`, which posts nothing; applications carry `AppliedOn` dates so aging and statements work as of a past date. Applying more of the same source to the same invoice adds to the existing application (keeps the first date).
- `CreditMemo` posts the mirror of an invoice (Dr income, Cr A/R) and shares the invoice number sequence. `CustomerRefund` pays credit back (Dr A/R, Cr bank/card) from one credit memo or one payment.
- `Estimate` posts nothing; `InvoiceService` closes it when invoiced (`CreateInvoiceCommand.EstimateId`) and reopens it when that invoice is voided.
- Undeposited funds: payments and receipts in an Other current asset account are grouped by `DepositService` into a `BankDeposit` (`Source = bank.deposit`: Dr bank, Cr holding account). Deposited payments/receipts can't be voided until the deposit is.
- Cash basis (`ReportLedger`): credit memos and refunds are accrual documents. Payments recognize applied amounts plus discounts against the invoice lines (credit applied later counts on the payment's date), keep the unapplied part in A/R, and include the discount lines as posted. A credit memo refund reverses income on the refund date; a payment refund counts as posted.
- `ReceivablesService` computes aging and statements from the documents, not the ledger. Forms: `FormService` builds `SalesForm`/`StatementForm`, `IFormRenderer` (MigraDoc) renders them. PDFsharp-MigraDoc is pinned to `[6.2.4]`, the last MIT release (6.5+ is AGPL or commercial): never let it be upgraded. Its Core build needs `SystemFontResolver`, which maps the one family forms use ("OpenBook Sans") to a system sans-serif, or to OpenBookSans-Regular/Bold.ttf in a `fonts` folder beside the app. PDFs are served inline at `/api/sales/{kind}/{id}/pdf`.
- Email: `EmailService` with `IEmailSender` (MailKit; "folder" delivery writes .eml files to `mail/` beside the company file). The SMTP password is encrypted by the host's `ISecretProtector` (Data Protection; tests use a marker). Every send writes an `AuditAction.Emailed` record. `RecurringPostingService` also creates (and emails) automatic recurring invoices.
- New audit entity types: estimate, credit_memo, refund, deposit, recurring_invoice, email_settings, form_style (all in `Links.ForAuditEntity`).

## Payables (V1.4)

- `VendorCredit` posts the mirror of a bill (Dr A/P, Cr categories) and reuses `BillLine`. `VendorCreditService.ApplyAsync` applies credit to the vendor's bills without posting (applications carry `AppliedOn`); `VendorRefund` records money the vendor pays back (Dr bank, Cr A/P). A bill's `AmountPaid` includes applied credits.
- `RecurringBill` stores bill line drafts and uses `Recurrence`; `RecurringBillService.CreateDueAsync` enters each bill through `BillService` (recorded as `Bill.RecurringBillId`). The hourly `RecurringPostingService` runs automatic ones.
- `PayablesService`: A/P aging (like A/R, from the documents) and the 1099 summary. 1099 totals are bill payments and vendor expenses dated in the calendar year, excluding card accounts (1099-K). `DefaultThreshold`: $600 through 2025, $2,000 from 2026 (later years are inflation-indexed; the report takes an override).
- Vendors have `Track1099`, `TaxId` (normalized EIN/SSN) and `Form1099Box`. The tax ID is never shown or returned in full: use `MaskedTaxId`; in `VendorDetails` a null `TaxId` keeps the saved one and "" clears it.
- Attachments (`Domain/Attachments`): metadata in `Attachments`, bytes in `AttachmentContents` (cascade delete), in the company file. The content type comes from the extension (`Attachment.AllowedTypes`: no HTML/SVG/scripts); only images and PDFs are served inline, always with `X-Content-Type-Options: nosniff` and a sandboxing CSP. Uploads go through the web form endpoint `/attachments` (antiforgery), not the cookie API. `AttachmentService.EntityTypes` lists what can take files, and `EfAttachmentStore.ExistsAsync` must know each type. Adding/removing writes `Attached`/`Detached` audit records on the owning record. Put `<Attachments EntityType=… EntityId=… />` on new detail pages.
- `BillLinesEditor`/`BillLineModel` (Components/Purchases) is the shared category grid for bills, vendor credits and recurring bills.

## Banking pro (V1.2)

- Statement files: `Ofx.Parse` (Application/Import) reads OFX/QFX/QBO, SGML or XML, with a regex tag scan (no XML parser). `BankFeedService.PreviewAsync` takes OFX or CSV; nothing posts at import.
- `BankFeedItem` is the review queue (Pending / Added / Matched / Excluded). `DedupeKey` is unique per account: `fit:<FITID>`, else a text key of date, amount, payee and reference with `#n` for the nth identical line in one file. Re-importing skips known keys.
- Add stages a `BankTransaction` through `BankingService`; Match links an existing ledger line of the same holder-view amount within 7 days; Reopen (undo) voids the transaction for an added line.
- `BankRule`: first active rule in `Priority` order whose text is in payee or memo (plus direction, amount range, account) wins. AutoAdd rules post at import; others only suggest. A vendor only goes on money-out rules.
- `ReconciliationService.UndoAsync` reopens only the latest completed reconciliation of an account, and only when none is in progress; ticks are kept.
- Checks: `CheckService` lists bank (not card) spends and bill payments; printing renders `CheckForm`s (voucher layout, absolutely positioned MigraDoc frames, `AmountInWords.ForCheck`) and sets `PrintedAt` with an audit record. Printing goes through the web form endpoint `/checks/print` (antiforgery), which binds `IFormCollection`: minimal APIs on .NET 10 can't bind a `string[]` form parameter.
- Blazor `[SupplyParameterFromQuery]` can't bind enums: take a string and `Enum.TryParse`.
- Audit entity types: bank_rule, bank_feed_item, bank_feed_import.
