# OpenBook Desktop

OpenBook on one Windows computer, for one business: the same program as [OpenBook Server](server.md), installed differently. (macOS isn't packaged yet.)

## Installing

Run `OpenBook-Setup-<version>.exe`. It installs for the current Windows user only, without administrator rights, into `%LOCALAPPDATA%\Programs\OpenBook`, and adds **OpenBook** to the Start menu. The installer isn't code-signed yet, so Windows SmartScreen may warn before it runs (**More info → Run anyway**).

Starting OpenBook runs it in the background and opens it in the browser at `http://localhost:5180` (another port if that one is taken). Starting it again just opens the browser. Only this computer can reach it. It keeps running, with an OpenBook icon in the system tray (the notification area by the clock, maybe under its ^ arrow), until you quit it or sign out of Windows. The tray icon's menu has **Open OpenBook** (also a double-click), **Back up now** and **Quit OpenBook**. Quitting there, or with **Settings → System → Quit OpenBook**, first makes a backup to the scheduled backups' destination (named `openbook-scheduled-quit-…`; the last 5 are kept, apart from the daily ones), then stops OpenBook. From the tray, if that backup fails you're asked whether to quit anyway. The tray talks to OpenBook through a named pipe only your Windows user can open; its name is in `running.json`.

Installing a newer version over it keeps everything; it stops a running OpenBook first. Uninstalling (Windows **Settings → Apps**) removes the program only.

## Moving to a new computer

Copy `Documents\OpenBook` and `%LOCALAPPDATA%\OpenBook\keys`. Without the keys folder the books are safe, but saved passwords, bank connections, ACH numbers and two-factor sign-in have to be set up again.

## Updates

An installed OpenBook Desktop checks GitHub for a newer release a minute after starting and then once a day, sending nothing but its version. It downloads a newer installer into `%LOCALAPPDATA%\OpenBook\updates`. It only keeps it when the release key signed the release and the file's size and SHA-256 match. Then **Install OpenBook x.y.z** appears in the tray menu (with a notice) and on **Settings → System → Updates**.

Installing backs up the books first, as Quit does; if that backup fails, you're asked whether to go on anyway. Then the installer runs silently (its `install.log` is beside the download), and OpenBook starts again with the same folders and port.

If an update stops partway and OpenBook won't start, your books are safe in Documents: run the installer in `%LOCALAPPDATA%\OpenBook\updates` again, or download the latest one from the releases page. When an update changes the company file, a `pre-upgrade-…` copy goes into the `Backups` folder first (the last three are kept).

**Settings → System → Updates** also has **Check now**, and turns the daily check off or on; the choice is kept in `%LOCALAPPDATA%\OpenBook\updates.json`. `OpenBook:Updates:Check` and `OpenBook:Updates:Feed` do the same in configuration. Publishing releases is described in [releasing.md](releasing.md).

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

- `OpenBook.Web.exe reset-password <username>`: a forgotten password (OpenBook quit first is safest). Add `--restore-admin` to make them an active admin, when nobody else can.
- `OpenBook.Web.exe encrypt-database` with `OpenBook__EncryptionKey` set: encrypt the company file. Quit OpenBook first (Settings > System > Quit OpenBook) and keep a backup; it refuses while OpenBook is running. `decrypt-database` undoes it. The original stays beside the file as a `.bak` until you delete it.
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
