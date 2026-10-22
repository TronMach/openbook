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
