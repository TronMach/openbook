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
- `ReportBasis` (Accrual/Cash) per report, default from company settings. `EntryBasis` on each entry (Both / AccrualOnly / CashOnly) decides inclusion. Payment-driven cash basis for invoices/bills comes with M3/M4.
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
- Invoices post to the first active A/R account; one is created and saved on first use if none exists. Payments are fully applied to the customer's invoices (no unapplied credits until V1.3) and deposit to a Bank or Other current asset account. Invoice `AmountPaid` is kept in step by payment create/void. An invoice with payments applied can't be voided.
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
