# OpenBook Desktop

OpenBook on one Windows computer, for one business: the same program as [OpenBook Server](server.md), installed differently. (macOS isn't packaged yet.)

## Installing

Run `OpenBook-Setup-<version>.exe`. It installs for the current Windows user only, without administrator rights, into `%LOCALAPPDATA%\Programs\OpenBook`, and adds **OpenBook** to the Start menu. The installer isn't code-signed yet, so Windows SmartScreen may warn before it runs (**More info → Run anyway**).

Starting OpenBook runs it in the background and opens it in the browser at `http://localhost:5180` (another port if that one is taken). Starting it again just opens the browser. Only this computer can reach it. It keeps running until **Settings → System → Quit OpenBook** or until you sign out of Windows.

Installing a newer version over it keeps everything; it stops a running OpenBook first. Uninstalling (Windows **Settings → Apps**) removes the program only.

## Where things are

| What | Where |
|---|---|
| Company file | `Documents\OpenBook\openbook.db` |
| Daily backups (on from the start, 14 kept) | `Documents\OpenBook\Backups` |
| Data Protection keys (stored passwords, two-factor sign-in) | `%LOCALAPPDATA%\OpenBook\keys` |
| Logs, add-ins | `%LOCALAPPDATA%\OpenBook\logs`, `%LOCALAPPDATA%\OpenBook\plugins` |
| Program | `%LOCALAPPDATA%\Programs\OpenBook` |
| QuickBooks export helper (Start menu: OpenBook - Export from QuickBooks) | `%LOCALAPPDATA%\Programs\OpenBook\QuickBooks\OpenBook.QuickBooksExport.exe` |

To keep the company file somewhere else, set the environment variable `OpenBook__Desktop__DataFolder` to a folder; the port can be changed with `OpenBook__Desktop__Port`. There are no database or address settings on the pages.

The program folder's `openbook.install.json` makes every run of it a desktop one, commands included. From a command prompt in the program folder:

- `OpenBook.Web.exe reset-password <username>`: a forgotten password (OpenBook quit first is safest).
- `OpenBook.Web.exe encrypt-database` with `OpenBook__EncryptionKey` set: encrypt the company file.
- `OpenBook.Web.exe move-to-postgres …`: move the company to OpenBook Server (see [Moving a desktop company in](server.md#moving-a-desktop-company-in)).

## Building the installer

On Windows, with the .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`):

```
powershell -File tools/package-desktop.ps1 -Version 1.0.0
```

It publishes `OpenBook.Web` (the server) and `OpenBook.Desktop` (the launcher: no console window, starts the server and leaves) self-contained for win-x64 into `artifacts/desktop/win-x64`, adds `openbook.install.json`, and compiles `packaging/windows/OpenBook.iss` into `artifacts/desktop/OpenBook-Setup-<version>.exe`. It also builds the QuickBooks Desktop export helper (`tools/package-qb-export.ps1`, below) into its `QuickBooks` folder. `-SkipInstaller` stops after publishing; that folder runs as it is. The icon is made from the app's icon by `python tools/make-icon.py`.

## The QuickBooks export helper

`src/OpenBook.QuickBooksExport` runs on the PC where QuickBooks Desktop has the company open and saves qbXML files for **Settings → Move in from another program** (user guide: [Moving in](../user-guide/24-moving-in.md#quickbooks-desktop-company-files-qbw-qbm-qbb)). It talks to QuickBooks through the request processor QuickBooks installs (`QBXMLRP2.RequestProcessor`), so neither Intuit's SDK nor .NET is needed on that PC.

```
powershell -File tools/package-qb-export.ps1 -Version 1.0.0
```

publishes it for win-x86 (it works with 32- and 64-bit QuickBooks) as one self-contained file, `artifacts/quickbooks-export/OpenBook.QuickBooksExport.exe` (about 70 MB). It isn't compressed: VIPRE quarantined the compressed, self-extracting build as a suspected packer. It isn't signed either, so antivirus may still distrust it until it is; on a build PC, exclude `artifacts` rather than the whole checkout.
