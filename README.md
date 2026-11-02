# OpenBook

Open-source bookkeeping for small businesses and nonprofits, in your browser, with your books in a file or database you control. Invoices and estimates, bills and payments, banking and reconciliation, inventory, sales tax, projects and time, fixed assets, budgets and reports, with a full audit trail and a ledger that always balances.

OpenBook comes in two installs of the same program:

- **OpenBook Desktop** for one computer: a Windows installer, the company file in your Documents folder, daily backups, and an icon in the system tray. A **simple view** with a QuickBooks Desktop-like home page suits a business one person runs.
- **OpenBook Server** for several people: a Docker image, with PostgreSQL, behind your own HTTPS proxy.

Moving in from QuickBooks Desktop or Online, GnuCash, Xero, Wave or FreshBooks is built in.

## Getting it

- **Windows:** download `OpenBook-Setup-<version>.exe` from [Releases](https://github.com/TronMach/openbook/releases) and run it. It installs for your Windows user, without administrator rights. The installer isn't code-signed yet, so Windows may warn before it runs (**More info → Run anyway**).
- **Docker:** `docker compose up -d` with the included `docker-compose.yml` (change its passwords first). See [docs/admin/server.md](docs/admin/server.md).
- **From source:** install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then `dotnet run --project src/OpenBook.Web` and open the address it prints.

## Documentation

- [User guide](docs/user-guide/README.md): every area, written for bookkeepers.
- [Desktop admin guide](docs/admin/desktop.md) and [server admin guide](docs/admin/server.md).
- [Roadmap](ROADMAP.md): what's built and what's planned.
- [CLAUDE.md](CLAUDE.md): how the code is organized and the rules it keeps, for contributors.

## Building and testing

```bash
dotnet build
```

```bash
powershell -File tools/run-tests.ps1
```

`tools/run-tests.ps1 -Postgres` runs the integration tests on PostgreSQL too (needs `OPENBOOK_TEST_POSTGRES`; see CLAUDE.md). `tools/package-desktop.ps1 -Version x.y.z` builds the Windows installer (needs Inno Setup 6).

## Licence

OpenBook is free software under the [GNU Affero General Public License, version 3 or later](LICENSE): you may use, study, change and share it, and if you change it and let others use it, including over a network, you must offer them your changes' source under the same licence. The fonts bundled for printed forms are under the SIL Open Font License; other components keep their own licences.
