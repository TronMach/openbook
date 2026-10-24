# OpenBook

Open-source QuickBooks alternative in .NET 10. Plan and milestones: `ROADMAP.md`.

## Commands

- Build: `dotnet build`
- Test: `dotnet test` (Domain.Tests = pure unit; Integration.Tests = migrated in-memory SQLite incl. triggers)
- Run: `dotnet run --project src/OpenBook.Web` (migrates `openbook.db` on startup; API under `/api`)
- Reset a forgotten password: `dotnet run --project src/OpenBook.Web -- reset-password [username]` (same config as the web app; prompts twice, reads lines when stdin is piped; refuses if the company file is missing rather than creating one)
- Encrypt / decrypt the SQLite company file in place (OpenBook stopped, key from `OpenBook:EncryptionKey`): `dotnet run --project src/OpenBook.Web -- encrypt-database` / `decrypt-database`
- New migration (SQLite): `dotnet ef migrations add <Name> -p src/OpenBook.Infrastructure -s src/OpenBook.Infrastructure -o Persistence/Migrations`
- New migration (PostgreSQL), every time a SQLite one is added: `dotnet ef migrations add <Name> -p src/OpenBook.Infrastructure.Postgres -s src/OpenBook.Infrastructure.Postgres -o Migrations`
- PostgreSQL tests run only with `OPENBOOK_TEST_POSTGRES` set (a server connection string allowed to create databases, e.g. `Host=localhost;Username=postgres;Password=…`); otherwise they're skipped. Add `OPENBOOK_TEST_ALL_ON_POSTGRES=1` to run the whole integration suite on PostgreSQL (each test gets its own database, dropped afterwards; `[SqliteFact]` tests such as file backups skip). Run it after schema or SQL changes.
- Docker: `docker build -t openbook .` (SQLite in `/data`), or `docker compose up -d` (with PostgreSQL). `/healthz` for probes.

## Layout

Domain (no EF/ASP.NET) → Application (use cases, `ILedgerStore`) → Infrastructure (EF Core; SQLite migrations) → Infrastructure.Postgres (PostgreSQL migrations only) → Api (minimal API endpoints, class library) → Web (Blazor host, maps the API).

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

## Sales tax (V1.5)

- Domain in `Domain/SalesTax`: `TaxAgency` (owns a liability account: a subaccount of "Sales Tax Payable", created by `SalesTaxService` on agency creation), `TaxRate` (one agency, percent to 4 decimals, stored as ten-thousandths), `TaxCode` (1–10 rates in print order; "combined" when more than one), `SalesTaxPayment` (`Source = salestax.payment`).
- Tax lines are `SalesLine`s of kind `Tax`, appended after the other lines by `SalesLineBuilder.BuildAsync(drafts, customer, taxCodeId, date)`: one per rate of the code, even when zero, with `TaxRateId`, `Rate` = the percent charged and `IncomeAccountId` = the agency's liability account, so `SalesLine.Postings` credits it and cash-basis allocation covers it. They're never drafted: use `SalesLine.Drafts(lines)` to copy a document's lines (`ToDraft()` throws on a tax line).
- `TaxCalculator` is the one place for the math (the web preview calls it too): taxable amount = taxable item lines less each discount's taxable share; tax = taxable × combined percent rounded once, split by rate with the remainder on the last.
- Command `TaxCodeId`: null takes the customer's code, `Guid.Empty` charges no tax. Documents store `TaxCodeId` and `TaxExemption` (the customer's exemption text when valid on the document date; tax lines are then zero). Recurring invoices use the customer's code; estimates pass theirs to the invoice.
- Line `Taxable` defaults from the item (`Item.Taxable`; existing items were backfilled taxable); `SalesLineDraft.Taxable` null means "the item's setting".
- Inactive codes, rates and agencies can't be charged (`tax.code_inactive`, …); a rate in an active code and an agency with active rates can't be deactivated.
- `SalesTaxReports.LiabilityAsync` works from the documents (accrual): invoices and receipts plus credit memos negated, each counted on every rate of its code; owed figures are the liability accounts' ledger balances. `FilingPeriods.LastCompleted` gives the period to file.
- `FormService.TaxTotals` gives the subtotal/tax rows for screens and PDFs; tax lines never show as ordinary lines.
- Audit entity types: tax_agency, tax_rate, tax_code, sales_tax_payment (attachments allowed on sales_tax_payment and now shown on customers, for certificates).

## Reports, budgets, tax lines (V1.7)

- Every report is a `ReportDefinition` in `ReportCatalog.All` (key, inputs, group) and runs through `ReportCatalog.RunAsync(key, ReportParameters)` to a `ReportTable` (columns with a `ColumnFormat`, rows with depth/style, cells with optional `ReportLink`). The screen (`ReportTableView`), CSV (`ReportCsv`), Excel and PDF (`IReportExporter`: ClosedXML, MigraDoc) all render that table, so exports match the screen. A new report = a definition + a case in `RunAsync`; it gets the generic page `/reports/{key}`, exports and saving for free. Query parameter names are the pages' own (`preset/from/to/basis/asOf/compare/accountId/year/threshold/detail/budgetId/fromMonth/toMonth`).
- Dedicated pages (trial balance, GL, journal, aging, 1099, sales tax) keep their markup and add `<ReportActions Key=…/>` for export/save. Literal `/reports/...` routes win over `/reports/{Key}`.
- Comparisons: `Comparisons.Columns` (previous period, previous year, months, quarters; max 24 columns). Multi-column statements build each column with `FinancialReports.BuildProfitAndLoss/BuildBalanceSheet` and a shared `show` set so every column has the same rows; `StatementTables` zips them and adds `DerivedColumn`s ($ change, %, total).
- Cash flow (indirect): cash = Bank accounts; operating = net income + current assets/liabilities (incl. A/R, A/P, cards); investing = fixed/other assets; financing = long-term liabilities and equity. Net change always equals the bank balance change.
- Sales by customer and expenses by vendor read `ReportLedger` (so they tie to the P&L on either basis) and attribute lines by `Source`/`SourceId` to the document's customer/vendor. Sales by item is document-based (accrual).
- `Budget` (Domain/Reporting): monthly amounts (natural sign, P&L accounts only) for one fiscal year; owned `BudgetLines`; deletable (audit `Deleted`). `SavedReport` stores report key + query string (presets stay relative); saving under an existing name updates it. Audit types `budget`, `saved_report`.
- Tax lines: `TaxLines` catalog per `TaxForm` (from `BusinessType`), keys stored in `Account.TaxLineKey` (P&L accounts only; cleared on a change to a balance sheet type). Unmapped subaccounts use the nearest parent's line in `StatementReports.TaxSummaryAsync`. Keys are stable; relabel lines for new form years, never rekey.
- Grid forms (budget amounts, tax line mapping) and save/delete of saved reports post to `Web/Endpoints/ReportEndpoints.cs` binding `IFormCollection`.
- ClosedXML is MIT (0.105.x); text cells are written as text so nothing exports as a formula, and CSV prefixes `=`, `+`, `@` and non-numeric `-` with `'`.

## Inventory (V1.6)

- `ItemType.Inventory` items carry `InventoryAssetAccountId` (other current asset), `CostAccountId` (expense class, usually COGS), `ReorderPoint` and `PurchaseCost`. `ItemService` fills missing accounts with "Inventory Asset" / "Cost of Goods Sold" (`InventoryAccounts`, created on first use; "Inventory Shrinkage" is the default adjustment account). Once an item has movements its type and asset account are locked.
- Stock is an append-only ledger of `InventoryMovement`s (Domain/Inventory). A receipt is its own FIFO layer (`LayerId == Id`); issues and reversals point at a layer. `Fifo.Layers` derives remaining quantity/cost; `Fifo.Take` uses the oldest layers received on or before the date, proportional cost rounded to the cent with the last of a layer taking exactly what's left. Stock can't go negative (`inventory.insufficient`).
- Documents move stock through `InventoryService` in three steps: plan (checks everything: `PlanSaleAsync`, `PlanReturnAsync`, `PlanPurchaseAsync`, `PlanReversalAsync`), stage the entry with the plan's postings appended, then `Apply`. Sales post Dr COGS / Cr inventory asset in their own entry; credit memos restock at the latest sale's average unit cost (else average on hand, else purchase cost). Voids add opposite movements on the document date and are refused once a layer the document opened has been used (`inventory.layer_used`). `InventorySources` names the document types (audit entity types).
- Bills take item lines (`BillLineDraft.ItemId`/`Quantity`): the line posts to the item's asset account and opens a layer at the line amount. Vendor credits and recurring bills refuse item lines. `BillLinesEditor` shows item columns when given `Items` (bills only).
- `InventoryAdjustment` (`Source = inventory.adjustment`, audit `inventory_adjustment`, attachments allowed): increases at a unit cost, decreases at FIFO cost; value change against the adjustment account (Opening Balance Equity for starting stock).
- Cash basis keeps the inventory cost lines of invoices and credit memos as posted (`ReportLedger`): COGS is recognized when stock is sold.
- `EfInventoryStore.ListMovementsAsync` includes unsaved added movements, so several documents in one save see each other's stock.
- Reports: `inventory-valuation` (as of a date, summary or layers, with a tie-out row to the asset accounts) and `inventory-stock` (reorder status) in `ReportCatalog`, built by `InventoryReports`.

## Multi-user and server (V1.8)

- `UserRole` (ViewOnly < Standard < Accountant < Admin) on `User`, plus `IsActive`; users are deactivated, never deleted. Changing a role or deactivating rotates the security stamp, so sessions end. `UserService` keeps at least one active admin and refuses changes to yourself. Inactive users sign in like a wrong password.
- Access is decided in one place: `Web/Security/AccessRules.Required(path, method)` (first matching prefix rule; reads need ViewOnly, changes and "write pages" — `new`, `edit`, `import`, `apply`, `pay`, `reconcile`, `feed`, recurring editors, … — need Standard), enforced by `AccessMiddleware` for pages, form endpoints and the API (403 JSON for `/api`, `/access-denied` redirect otherwise). A new page or endpoint that changes the books or settings needs a rule; add a case to `ApiAccessTests.Access_rules_map_paths_to_roles`. Hide nav links with `User.Can(role)`.
- The role is a claim in the cookie; cookies without one (from before V1.8) are rejected so people sign in again.
- API keys (`ApiKey`, `ApiKeyService`): `Authorization: Bearer obk_…`, SHA-256 hash stored, shown once, act as their user with the user's role, revoked not deleted, last use recorded at most once a minute. The default auth scheme is a policy scheme picking the bearer handler (`ApiKeyAuthenticationHandler`) or the cookie.
- OpenAPI at `/api/openapi/v1.json` (anonymous; Microsoft.AspNetCore.OpenApi). `/api/me`, `/api/users`, `/api/api-keys`, `/api/webhooks`, `/api/backup` cover what the new pages do.
- Webhooks are fed by the audit log: `WebhookDispatcher` (run every 5 s by `WebhookDispatchService`) turns audit records after each subscription's `Cursor` into `WebhookDelivery` rows (event type `{entity_type}.{action_snake}`, payload with before/after snapshots, fixed at creation), then posts due ones via `IWebhookSender` (`HttpWebhookSender`: 10 s timeout, no redirects) with `OpenBook-Signature: t=…,v1=HMAC-SHA256(secret, "t.body")` (`WebhookSignature`). Retries back off 1 m → 12 h, 6 attempts, then Failed (redeliver by hand). Secrets are stored with `ISecretProtector`. Anything audited is an event, so keep audit snapshots free of secrets.
- Encryption at rest: SQLite runs on SQLCipher (`SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 with `Microsoft.EntityFrameworkCore.Sqlite.Core`). `OpenBook:EncryptionKey` becomes the connection's `Password`; backups and safety copies use the same key, and restore only accepts backups with that key. `SqliteEncryption` converts files with `sqlcipher_export` (triggers included).
- PostgreSQL: `OpenBook:Database=Postgres` (or a connection string starting `Host=`). Migrations live in `OpenBook.Infrastructure.Postgres` (`InitialPostgres` creates the whole current schema; ledger triggers as PL/pgSQL functions in `InitialPostgres.Triggers.cs`, same names and messages, plus a TRUNCATE guard on AuditRecords). Every schema change needs a migration in both projects, and trigger changes in both dialects. In-app backup/restore is SQLite-only (`UnsupportedBackupService` says to use pg_dump). Store `DateTimeOffset`s in UTC (Npgsql rejects other offsets). The whole integration suite passes on PostgreSQL 17. Tests that touch the database directly catch `System.Data.Common.DbException` and use SQL both providers accept (TRUE/FALSE, explicit `"Basis"`).
- Containers: Data Protection keys go to `OpenBook:DataProtectionKeys` (Docker: `/data/keys`) so cookies and stored secrets survive restarts. The image installs DejaVu fonts for PDFs and `libgssapi-krb5-2` (Npgsql loads it on connect) and runs as the non-root `app` user. The build copies `.editorconfig`, which marks migrations as generated code; without it the analyzers fail the Release build. Image and compose stack verified with Docker Desktop (Linux containers); on a fresh PostgreSQL database EF logs one failed `__EFMigrationsHistory` query before creating the schema, which is expected.
- Audit entity types: api_key, webhook (users were already `user`).

## Portability and UX (V1.9)

- Imports post journal entries with `JournalEntry.ImportSource` ("ledger.import", a journal source, so voided from the journal), `SourceId` = the import batch. `JournalImportService`: journal entry CSV (date, account by full name or number, debit/credit or signed amount, optional entry number; without one, lines group until they balance; dates' order detected per column) and opening balance CSV (natural-sign balance or debit/credit, through `OpeningBalanceService`). Preview first; any error stops the whole import. `PostingEngine.SaveAsync` saves staged entries for callers without a document.
- `IifImportService` reads QuickBooks Desktop IIF (tab-separated, `!TYPE` headers): ACCNT, CUST, VEND, INVITEM and TRNS/SPL/ENDTRNS. Existing names are skipped; NONPOSTING accounts, estimates/orders and discount/subtotal/tax items are skipped; inventory parts become non-inventory items; transactions become journal entries (positive = debit), so A/R and A/P aging won't list them. It creates records through the normal services inside an `IUnitOfWork` transaction (EF: `EfUnitOfWork`; `OpenBookDbContext.SaveChangesAsync` joins an open transaction), so the file imports whole or not at all.
- Full data export (`IDataExporter` → `DataExporter`): a ZIP with one CSV per table built from the EF model (raw `SELECT` with quoted identifiers, values as stored), `attachments/`, and a generated README documenting every table and column (types as stored, cents/ten-thousandths, foreign keys). Secrets are excluded by table/column in `DataExporter.Excluded` (password and token hashes, protected secrets, vendor tax IDs, attachment bytes); add new secret columns there. Admin only (`/export/download`, `/api/export`).
- Notes (`Domain/Notes/Note`, `NoteService`): free text on any record in `NoteService.EntityTypes` (attachment types plus account and item), audited on the owning record as `NoteAdded` / `NoteRemoved`. Put `<Notes EntityType=… EntityId=… />` beside `<Attachments>` on record pages; it posts to `/notes` (Web endpoint, antiforgery).
- Register for any account: `/accounts/{id}/register` (general ledger lines in the account's natural direction with a running balance) with a quick-entry row that posts a two-line journal entry (accountant role).
- `wwwroot/shortcuts.js`: theme choice (Auto/Light/Dark in `localStorage` `ob-theme`, `data-theme` on `<html>`, set early by an inline script in `App.razor` and re-applied after enhanced navigation) and keyboard shortcuts (`/` search, `?` help, `g`/`n` + key to go or create, Ctrl+Enter submits the current form, Alt+Q quick entry; ignored while typing). Dark colors live under `:root[data-theme="dark"]` and the `prefers-color-scheme` block (skipped when `data-theme="light"`); keep both in step.
