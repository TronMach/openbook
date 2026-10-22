# OpenBook

Open-source QuickBooks alternative in .NET 10. Plan and milestones: `ROADMAP.md`.

## Commands

- Build: `dotnet build`
- Test: `dotnet test` (Domain.Tests = pure unit; Integration.Tests = migrated in-memory SQLite incl. triggers)
- Run: `dotnet run --project src/OpenBook.Web` (migrates `openbook.db` on startup; API under `/api`)
- New migration: `dotnet ef migrations add <Name> -p src/OpenBook.Infrastructure -s src/OpenBook.Infrastructure -o Persistence/Migrations`

## Layout

Domain (no EF/ASP.NET) → Application (use cases, `ILedgerStore`) → Infrastructure (EF Core) → Api (minimal API endpoints, class library) → Web (Blazor host, maps the API).

## Ledger rules (do not bypass)

- Everything that affects balances posts through `PostingEngine` as a `JournalEntry`. No other code writes `JournalLines`.
- Money is `decimal` (or `Money`) in code and integer minor units (`AmountMinor`) in the DB. Never `double`/`float` in Domain (`ArchitectureTests` enforces).
- Posted entries are immutable. Corrections = `VoidAsync`, which posts a same-date reversal. Never delete.
- Every change writes an `AuditRecord` in the same save.
- DB triggers (`SqliteLedgerTriggers`) enforce balance-on-seal, immutability and append-only audit. New entries are inserted unsealed and sealed in the same transaction by `OpenBookDbContext.SaveChangesAsync`. A schema change touching these tables must keep the triggers in sync.
- Keep SQL provider-neutral (quoted identifiers, no SQLite-only functions) for the V1.8 PostgreSQL provider.
- Domain errors throw `DomainException` with a stable `code`; the API maps them to 400 (404 for `EntityNotFoundException`).

## Company, sign-in, backup

- One company per database, created by `/setup` (`CompanySetupService`). Home currency comes from the `Company` row; ledger services throw `company.not_set_up` before setup. `SetupGateMiddleware` routes pages to `/setup` until then.
- Cookie auth (`Web/Security/WebSecurity.cs`). The cookie carries the user's `SecurityStamp`, checked on every request, so changing a password or restoring a backup ends other sessions. Pages require auth via `Components/Pages/_Imports.razor`; mark public pages `[AllowAnonymous]`. The `/api` group requires auth and returns 401 instead of redirecting.
- Audit actor comes from `ICurrentUser`, which the host registers (`WebCurrentUser` covers both HTTP requests and interactive circuits).
- Backup = SQLite online backup API → plain `.openbook.db` file. Restore validates (integrity, known migrations, triggers present, company present, ledger balanced), writes a safety copy to `backups/` next to the company file, then copies over the live DB in place. A new trigger must be added to `SqliteLedgerTriggers.Names` or restore won't check for it.
- Pages are static server-rendered; use plain HTML forms with `<AntiforgeryToken />` for posts to minimal API endpoints (`Web/Endpoints`).
- Dev preview: `.claude/launch.json` → `openbook-web` on port 5188, data in `src/OpenBook.Web/openbook.dev.db`.
