# OpenBook Server for Windows

For an office where several people work on the books and one Windows computer can stay on. The other computers need nothing installed: they open OpenBook in a browser. It's the same program as [OpenBook Desktop](desktop.md), from the same installer, run as a Windows service. For a Linux server, or a server people reach over the internet, use the [Docker image with PostgreSQL](server.md) instead.

## Installing

On the computer that will keep the books (Windows 10 or later, or Windows Server 2016 or later, 64-bit):

1. Run `OpenBook-Setup-<version>.exe` and choose **Several people on our network**. Windows asks for an administrator. (The installer isn't code-signed yet, so SmartScreen may warn first: **More info → Run anyway**.)
2. When this person keeps a company with OpenBook Desktop here, the installer offers to bring it to the server. Quit OpenBook Desktop first (its tray icon → **Quit OpenBook**).
3. The last page gives the address for the other computers, such as `http://office-pc:5190`, and opens OpenBook. A new server shows **Set up your company** with the setup code filled in.
4. Add people under **Settings → Users and invitations**, and bookmark the address on their computers.

**OpenBook Server** in the Start menu opens it again. **Settings → System** shows the address whenever you need it, also by number (`http://192.168.1.20:5190`) for phones and other devices that don't find the computer by name.

Keep the computer on and awake while people work: in Windows **Settings → System → Power & sleep**, set **Sleep** to **Never** when plugged in. OpenBook starts with Windows, before anyone signs in, and Windows starts it again if it stops unexpectedly.

### If another computer can't open it

- **The network is "Public" in Windows.** The installer lets in computers on private and domain networks only. On the server: **Settings → Network & Internet**, the connection's properties, **Private**. Don't do that on a café's or a hotel's network.
- **The name isn't found.** Use the address by number from **Settings → System**. Ask for a fixed address for the server in the router (a DHCP reservation), or it may change.
- **Another firewall.** Some antivirus programs have their own: allow incoming TCP on port 5190.

## What the installer sets up

| What | Where |
|---|---|
| Program | `C:\Program Files\OpenBook Server` |
| Company file, backups, keys, logs, add-ins, settings | `C:\ProgramData\OpenBook` |
| Service | `OpenBook` ("OpenBook Server"), starts with Windows, runs as `NT SERVICE\OpenBook` |
| Firewall rule | "OpenBook Server": incoming TCP on its port, private and domain networks |

Only administrators, Windows itself and the service can open `C:\ProgramData\OpenBook`. If that folder was there before and an administrator didn't make it, the installer refuses to use it.

The port is 5190, or the next free one when something else already uses it on a first install.

Unattended: `OpenBook-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /ALLUSERS`, with `/PORT=5200` for another port and `/BRINGDESKTOP=1` to bring in the person's Desktop company (both on a first install only). Without `/ALLUSERS` it installs OpenBook Desktop. Exit code 20 means the program is in place but the server couldn't be set up as asked (`/LOG=<file>` says why). The setup code is then in `C:\ProgramData\OpenBook\setup-code.txt`, until the company is set up.

## How many people

The company file is SQLite: one person's save waits while another's finishes, which takes hundredths of a second. Measured on 2026-11-27, on a fast desktop computer with a company of two years and 3,700 invoices. Each person opened two pages and saved an invoice every nine seconds or so, for a minute or more: a much busier pace than people keep. The ranges are two runs.

| People at that pace | Pages (95% within) | Saves (95% within) | Refused saves |
|---|---|---|---|
| 5 | 0.09–0.17 s | 0.04–0.18 s | none |
| 20 | 0.11–0.22 s | 0.03–0.08 s | none |
| 40 | 0.12–0.19 s | 0.03–0.04 s | none, of 420 or more |

With 20 people saving invoices one after another without a pause, none was refused either, and 95% of saves took under half a second. Invoices and other documents saved at the same moment take numbers in turn, so two never get the same one. So do customer payments, bills, bill payments, bank entries, deposits and journal entries: none of them is refused because another took its entry number.

So an office of 20 is no strain. For many more, or for people working over the internet, use [PostgreSQL](server.md); `OpenBook.Web.exe move-to-postgres` moves the company there.

## Backups

On from the start: every day from noon into `C:\ProgramData\OpenBook\Backups`, the last 14 kept. That folder is on the same computer as the books, so also do one of these:

- **Settings → Backup & restore → Save to → S3-compatible storage**, or
- another folder there, such as a second drive. The service needs permission to write to it: `icacls "D:\OpenBook backups" /grant "NT SERVICE\OpenBook:(OI)(CI)M"`. On a network share it goes as the computer's own account, so a share on another computer works only in a Windows domain, or
- have your usual backup program copy `C:\ProgramData\OpenBook\Backups`.

Keep a copy of `C:\ProgramData\OpenBook\keys` as well, as private as the backups: stored passwords, bank connections and two-factor sign-in can't be read without it.

Before an update changes the company file, a `pre-upgrade-…` copy goes into `Backups` too (the last three are kept).

## Updates

OpenBook checks GitHub for a newer release once a day, sending nothing but its version, and says so on **Settings → System → Updates** with a link to the new installer. Run it on the server computer and choose **Several people on our network** again. It stops OpenBook for a minute or two, updates it and starts it again; the books and settings stay. Tell people first: typing they haven't saved is kept only on the forms that offer it back. **Stop checking automatically** on the same page turns the check off.

The service doesn't install updates by itself: its account has no rights to change programs, on purpose.

## HTTPS

OpenBook Server speaks plain HTTP on the office network: passwords and the books can be read by someone able to listen in on it. On a wired network, or Wi-Fi only your people use, many offices accept that. It isn't good enough for guest Wi-Fi shared with strangers, and **never** forward the port from your router to the internet.

These need HTTPS, or work only in a browser on the server itself at `localhost`: passkeys, installing OpenBook as an app from the browser, and offline capture on phones. Single sign-on and Microsoft mail need it too, when the provider refuses an address without it.

- **A virtual private network with certificates**, such as Tailscale, gives HTTPS and reaches people at home too, without opening anything to the internet.
- **Your own certificate** (`.pfx`), in `C:\ProgramData\OpenBook\settings.json`:

  ```json
  {
    "OpenBook": {
      "WindowsServer": { "Port": 5190, "Address": "https://books.example.com:5190" }
    },
    "Kestrel": {
      "Endpoints": {
        "Https": {
          "Url": "https://*:5190",
          "Certificate": { "Path": "C:\\ProgramData\\OpenBook\\books.pfx", "Password": "…" }
        }
      }
    }
  }
  ```

  Put the certificate in the folder, so only the service can read it, and restart the service. Change the Start menu shortcut's address by hand.

## Settings

`C:\ProgramData\OpenBook\settings.json`, opened as an administrator (Notepad → **Run as administrator**). Any OpenBook setting can go in it, as JSON (`"OpenBook": { "Alerts": { "To": "…" } }`; the list is in [the server guide](server.md#settings-worth-knowing)). Restart the service afterwards: **Services → OpenBook Server → Restart**, or `net stop OpenBook` and `net start OpenBook`.

- `OpenBook:WindowsServer:Port`: the port. Change it in the firewall rule too, or run `OpenBook.Web.exe install-service` (below), which does.
- `OpenBook:WindowsServer:Address`: the address **Settings → System** and invitations give, when it isn't the computer's name and port.

The database, backup and key folder settings are ignored: they're always in `C:\ProgramData\OpenBook`.

## Commands

From a command prompt opened with **Run as administrator**, in `C:\Program Files\OpenBook Server`. Those marked * need the service stopped first (`net stop OpenBook`, then `net start OpenBook`), and refuse otherwise.

- `OpenBook.Web.exe reset-password <username>`: a forgotten password (`--clear-two-factor`, `--restore-admin`).
- `OpenBook.Web.exe verify-audit`: checks that the audit trail is as it was written.
- `OpenBook.Web.exe restore-backup <file>`\*: a backup too large for the Backup page.
- `OpenBook.Web.exe bring-desktop-company [--from <file>] [--keys <folder>]`\*: brings a company from OpenBook Desktop into a server that has none yet. The defaults are the person's own `Documents\OpenBook\openbook.db` and `%LOCALAPPDATA%\OpenBook\keys`; give both for another person's, or for copies from another computer. An encrypted file's key goes in `settings.json` first (`"OpenBook": { "EncryptionKey": "…" }`).
- `OpenBook.Web.exe move-to-postgres …`\*: moves the company to a PostgreSQL server ([Moving a desktop company in](server.md#moving-a-desktop-company-in); the keys are in `C:\ProgramData\OpenBook\keys`).
- `OpenBook.Web.exe encrypt-database`\*, `decrypt-database`\*, `rekey-database`\*, `rekey-secrets`\*: as in [the server guide](server.md), with the key in `settings.json`.
- `OpenBook.Web.exe install-service`: puts the service, the folder's permissions and the firewall rule right, and starts it. `stop-service` and `remove-service` are what the installer uses; `remove-service` leaves the books.

## Moving to another computer

Stop the service and copy the whole of `C:\ProgramData\OpenBook` somewhere safe. Install OpenBook Server on the new computer without setting up a company, `net stop OpenBook`, put the copied files into its `C:\ProgramData\OpenBook` as an administrator, run `OpenBook.Web.exe install-service` (which gives the service its permissions to them), and open it. Then uninstall it on the old one.

## Uninstalling

Windows **Settings → Apps → OpenBook Server**. The service, the firewall rule and the program go; `C:\ProgramData\OpenBook` stays, with your books, until you delete it.

## If it doesn't start

The newest file in `C:\ProgramData\OpenBook\logs` says why. Without one, look in Windows **Event Viewer → Windows Logs → Application**. Most often another program uses its port: change it as above.

## Testing the installer

`tools/test-server-install.ps1` installs, checks, updates and uninstalls it on a computer that can be thrown away. The **Installer** workflow (`.github/workflows/installer.yml`) runs it on GitHub when the installer or what it uses changes.
