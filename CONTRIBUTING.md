# Contributing to OpenBook

Thank you for helping. OpenBook is bookkeeping software people trust with their books, so changes are held to a few firm rules; this page says what they are and how to work on the code.

## Before you start

- **Bugs**: open an issue with the bug form. Never attach a real company file or real customer details: make the problem happen in a test company (`make-demo`, below, builds one).
- **Security problems**: don't open an issue; see [SECURITY.md](SECURITY.md).
- **New features**: open an issue to talk it over first, so a pull request doesn't go to waste. [ROADMAP.md](ROADMAP.md) says what's planned.

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). On Windows, PowerShell runs the scripts; elsewhere install PowerShell (`pwsh`).

```bash
dotnet build
```

```bash
powershell -File tools/run-tests.ps1
```

```bash
powershell -File tools/run-tests.ps1 -Filter "FullyQualifiedName~InvoiceTests"
```

Browser tests drive Microsoft Edge or Chrome if one is installed (they never download a browser) and are skipped otherwise; set `OPENBOOK_TEST_BROWSER=0` to skip them anyway. The script runs a few tests at a time at low priority, so your computer stays usable. A run of the whole suite on SQLite takes several minutes. The PostgreSQL run (`-Postgres`) needs PostgreSQL installed (its programs, under `C:\Program Files\PostgreSQL`): the script sets up a server of its own for the tests in `artifacts\pgtest`, on port 5433. If you don't have PostgreSQL, the pull request's checks run it for you.

Run OpenBook with `dotnet run --project src/OpenBook.Web` and open the address it prints. A company with three years of history to try things on:

```bash
dotnet run --project src/OpenBook.Web -- make-demo --ConnectionStrings:OpenBook="Data Source=artifacts/demo.db"
```

## How the code is organized

[CLAUDE.md](CLAUDE.md) is the map: the projects, the commands, the rules every change keeps, and an index of the area notes in [docs/dev](docs/dev). Read the note for the area you're changing; it says what the code relies on and why.

The rules that are never bent:

- Everything that changes balances posts through `PostingEngine` as a journal entry. Posted entries are never changed or deleted: they're voided with a reversal.
- Money is `decimal` in code and whole cents in the database, never `double` or `float`.
- Every change writes an audit record in the same save.
- A database change needs a migration for SQLite and one for PostgreSQL. Never edit a migration that has shipped.

## Pull requests

- One change per pull request, with tests for what it changes. Every pull request runs the build, the whole suite on SQLite and PostgreSQL, and the package licence check.
- When a change renames something on screen, moves a page or changes a rule, update the [user guide](docs/user-guide/README.md) in the same pull request. It's written for bookkeepers, with the labels exactly as the pages show them.
- Update the area note in `docs/dev` when you change how an area works.
- Words on screen are plain English for bookkeepers: no ledger jargon where an everyday word does.
- Dependencies must be under a licence compatible with the AGPL (MIT, BSD, Apache-2.0 and the like). `tools/check-licences.ps1` checks every package, including those brought in by others; never add a proprietary or GPL-incompatible one.

## Licence

OpenBook is under the [GNU Affero General Public License, version 3 or later](LICENSE). By contributing, you agree that your contribution is under the same licence and that you have the right to offer it.

Please follow the [code of conduct](CODE_OF_CONDUCT.md) in issues, pull requests and anywhere else around the project.
