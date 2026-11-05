# People, backups and safety

## Users and roles

Administrators add people on **Settings → Users and invitations**. Each person signs in with their own username, so the audit log shows who did what.

**Add a user** asks for a username, a starting password (at least 8 characters; ask them to change it after signing in) and a role:

| Role | Can |
| --- | --- |
| View-only | See lists, documents and reports; change nothing |
| Standard | Also enter and void day-to-day work: invoices, payments, bills, expenses, bank imports and reconciling, time |
| Accountant | Also the books themselves: chart of accounts, journal entries and opening balances, imports, closing the books, most settings, the audit log, budgets, accountant reports |
| Admin | Also people and the system: users, email and online payment settings, webhooks, backups and restore, data export, add-ins |

Pages a role can't use are left out of its menu. On pages, buttons and forms it can't use stay visible but greyed out, saying what they need; selecting one opens a page that says which access is missing and names the administrators who can give it. They work again as soon as an administrator changes the role (the person signs in once more).

From the Users page an administrator can change someone's role, **reset their password** or **make them inactive** when they leave. Changing a role or making someone inactive signs them out everywhere at once, and a form they still have open stops working: saving it tells them they've been signed out. People are never deleted, so their name stays on what they entered. There's always at least one active administrator, and you can't change your own role or deactivate yourself.

## Two-factor sign-in

Two-factor sign-in asks for a 6-digit code from an authenticator app (Google Authenticator, Microsoft Authenticator, 1Password, Authy and others) after the password, so someone who learns your password still can't get in.

**Turn it on:** select your username in the top bar, then **Two-factor sign-in** (or **Settings → Your two-factor sign-in**). Scan the QR code with your app (or type the key shown under it), enter the code the app shows and select **Turn on two-factor sign-in**. OpenBook then shows ten **recovery codes**: save them somewhere safe (print them or keep them in a password manager). Each one signs you in once if you lose your phone; they aren't shown again.

**Signing in:** after your password, enter the code your app shows for OpenBook, or one of your recovery codes. Tick **Remember this browser for 30 days** on a computer or phone that's yours, to skip the code there. Changing your password or **Sign out all other sessions** forgets every remembered browser. Wrong codes count like wrong passwords: after five in a row the account is locked for five minutes.

On the same page you can **Make new recovery codes** (the old ones stop working) or **Turn off two-factor sign-in**; both ask for your password and a code. To move to a new phone, turn it off and set it up again.

**Lost your phone and your recovery codes?** An administrator can select **Turn off** in the **Two-factor** column on **Settings → Users and invitations**; that also signs you out everywhere. If no administrator can sign in, the `reset-password` command with `--clear-two-factor` does it (see [Forgotten password](01-getting-started.md#forgotten-password)).

**Requiring it (administrators):** **Settings → Sign-in and two-factor** → **Require it for**: nobody (each person decides), admins, accountants and admins, everyone but view-only users, or everyone. People in those roles who haven't set it up are taken to the setup page at their next sign-in and can't do anything else until they have; the page lists who that is. API keys keep working without a code.

### Inviting someone

To bring someone in, such as an outside accountant, invite them rather than making up a password for them: **Settings → Users and invitations** → **Invite someone**. Enter their email (and a name if you like), choose a role (or a permission set, listed under **Permission sets** in the same list), and optionally **Access ends on**, for someone who needs the books for a while, like tax season. **Link works for** sets how long the invitation stays open (7 days unless you choose otherwise).

**Send invitation** emails them a link when email is set up (**Settings → Email**); either way the page shows the link once, to copy and send yourself. Following it, they choose their own username and password and are signed in with the role you gave them. If their role must use two-factor sign-in, they set it up next.

The **Invitations** list shows each one as waiting, accepted, expired or withdrawn. **Send again** makes a new link (the old one stops working); **Withdraw** cancels it. Each link works once.

### Email and end of access

Select a username on the Users page to set their **Email** and **Access ends on**. From the day access ends, they can't sign in, their API keys stop working, and within the hour they're made inactive (signed out everywhere). Clear the date to keep them on. You can't end your own access, and there must always be an administrator whose access doesn't end.

### Permission sets

When the four roles are too coarse (a front-desk clerk who should enter sales but not see the bank, say), give someone a **permission set** instead of a role. Create sets on **Settings → Users and invitations** → **Permission sets** → **New permission set**: a name, and a level for each area of the books:

| Level | Means in that area |
| --- | --- |
| None | Its pages are left out of the menu and refused |
| View only | Look, change nothing |
| Standard | The day-to-day work, as the standard role does it |
| Accountant | Also that area's settings and the books themselves, as the accountant role does it |

The areas are accounts and journal (including company settings and the audit log), customers, items and sales, vendors and purchases, the document inbox, banking, inventory, projects, time and mileage, sales channels, fixed assets and loans, nonprofit, reports (including the dashboard), offline capture, and imports, exports, notes and custom field values. Pages outside every area (home, search, your own password, two-factor and API keys) are open to everyone; search only lists what a person can open. The notes, custom field and "print later" boxes on record pages belong to the imports, exports, notes and custom field values area: give it standard for someone who should add notes or fill in custom fields.

To give someone a set, choose it in their **Role** list on the Users page (under **Permission sets**) and select **Change**; choose a role there to take it away. Admin things (users, backups, data export, webhooks, add-ins, system settings) always need the admin role, so no set reaches them. A set counts as its highest level where OpenBook asks for a role, for example when two-factor sign-in is required "for accountants and admins".

Changing a set signs out everyone who has it, so their next sign-in uses the new levels. A set can be switched off once nobody active uses it. API keys of someone with a set reach only the set's areas (and a limited key narrows that further).

## Single sign-on

People can sign in with their Microsoft (Entra ID), Google or other OpenID Connect account instead of an OpenBook password. Administrators set it up on **Settings → Sign-in and two-factor** → **Single sign-on**:

1. At the provider, register OpenBook as a web application. Give it the **redirect address** the page shows (it ends in `/signin-oidc`). The provider gives you a **client (application) ID** and a **client secret**.
2. Back in OpenBook, choose the **Provider**. For Microsoft, enter your **directory (tenant) ID or domain**; for another provider, its **issuer address** (https). Enter the client ID and secret (the secret is stored encrypted and never shown again; leave it empty later to keep it), tick **Turn on single sign-on** and select **Save**.

The sign-in page then has a **Sign in with …** button (change its text with **Button text**). Only people OpenBook already knows get in:

- someone who signed in that way before;
- a user whose **Email** (on their user page) is the email the provider vouches for; their account is linked from then on;
- someone with a waiting invitation for that email: signing in accepts it and creates their user (they have no OpenBook password).

Anyone else is told to ask an admin for an invitation. Users can also link their account themselves: select their username in the top bar, then **Password and sessions**, then **link your account** under **Single sign-on**. An administrator can **Unlink** an account on the user's page.

Microsoft vouches for emails only with a named directory (not "organizations" or "common"); Google always does. With another provider, people may need to link their account themselves the first time.

OpenBook's own two-factor code isn't asked for after single sign-on: the provider does its own checks. Inactive users and users whose access has ended are refused as usual.

**Single sign-on only**: tick it to make everyone but administrators use the provider; their passwords stop working here. Administrators keep their password (and two-factor) as a way in when the provider is down or set up wrongly.

### Working at the same time

Several people can work in the books at once. If two of you save a change to the same thing at the same moment (say, both receive a payment on one invoice, both post the same bank line, or both sell the last units of a product), OpenBook usually tries yours again with the other change included, so it still checks for overpayments, stock that's run out and anything already done. If it still can't save yours, nothing of yours is saved and you see "Someone else saved a change to this at the same moment, so nothing was saved. Try again." Reload the page to see what changed, then enter it again if it's still needed.

## Closing the books

Once a period is finished (a month you've reconciled, a year your accountant has filed taxes for), close it so nobody changes it by accident: **Settings → Closing date** (also **Accounts → Close the books**).

- **Close the books through**: the last date that's closed. Nothing dated on or before it can be entered, voided or changed. That includes applying credit on such a date; a company that reports on the cash basis also needs the period open to apply credit from a payment dated in it, because that income counts on the payment's date. Leave it empty to reopen every period.
- **Closing date password** (optional, at least 6 characters, separate from sign-in passwords): with one, someone who knows it can still change a closed period by typing it when OpenBook asks. Without one, closed periods can't be changed at all until the closing date is moved. Every change made with the password is recorded in the audit log.
- A closing date more than a year ahead shows a warning, in case the year was mistyped.
- Moving the closing date earlier, clearing it, or changing or removing the password needs the current closing password.

**Fiscal years** on the same page lists each year with its net income. Closing a year moves the closing date to its last day. No closing entries are posted: the balance sheet carries each past year's net income into retained earnings automatically.

## The audit log

**Settings → Audit log** lists every change to the books: who made it, when, and what changed, field by field. Filter by type of record, user and dates. The log can't be edited or deleted, even by an administrator. Most record pages have a **History** link that opens the log for just that record.

## Backups

If you use the single company file (not PostgreSQL), administrators back it up on the **Backup** page (**Settings → Backups**; in the simple view, **Company → Back Up Company**).

### Download a backup

Downloads a copy of the whole company file, attachments included, as a `.openbook.db` file. Keep copies somewhere other than the computer running OpenBook.

### Scheduled backups

Set OpenBook to back itself up:

- **How often**: daily or weekly, at an hour in the company's time zone (**Settings → Time zone**; the server's if none is set).
- **Keep the newest**: how many to keep; older scheduled backups are deleted.
- **Save to**: a **folder** (give the full path, ideally a different drive or a synced folder), or **S3-compatible storage** such as Amazon S3, Backblaze B2, Cloudflare R2, Wasabi or MinIO (service URL, region, bucket, folder and an access key that can only put, list and delete in that bucket; the secret is stored encrypted).

**Save schedule**, then **Back up now** to check it works. The page shows the outcome of the last run. The first scheduled backup runs right after you save.

On **OpenBook Desktop** this is already on: every day from noon to the `Backups` folder beside your company file (`Documents\OpenBook\Backups`), keeping 14. Backups only run while OpenBook is running; if the computer was off at noon, the next start makes one. Change it like any schedule, and consider a folder on another drive or one your cloud storage syncs.

### Restoring a backup

**Restore from a backup**: choose the backup file, type your password and select **Restore backup**. OpenBook checks the file first (that it's an OpenBook company file, undamaged, and balances) and refuses it otherwise. Before replacing anything it saves a safety copy of the current books in a `backups` folder next to the company file, so a restore can itself be undone. Everyone else is signed out.

### With PostgreSQL

When the company is in PostgreSQL (usually OpenBook Server), the Backup page shows how to back it up with PostgreSQL's own tools instead of the download, schedule and restore buttons: the `pg_dump` command to schedule on the server and the `pg_restore` steps to bring a backup back, with your database's name filled in. **Export all data** works either way.

### Encrypted company files

The company file can be encrypted, so a stolen copy can't be read without the key. This is set up by whoever runs the OpenBook server (the `OpenBook:EncryptionKey` setting). Backups of an encrypted file are encrypted with the same key: keep the key safe, because without it neither the file nor its backups can be opened.

## Exporting all your data

**Backup → Export all data** downloads a ZIP with every table as a CSV file, every attachment, and a README describing each file and column. It's for taking your data elsewhere or analysing it in a spreadsheet; it isn't a backup you can restore. Passwords, keys and other secrets are left out, as are vendors' tax IDs.

## Desktop or Server

OpenBook comes in two installs of the same program:

- **OpenBook Desktop** runs on one computer. The company file is `Documents\OpenBook\openbook.db`, backups go to `Documents\OpenBook\Backups` every day, and OpenBook can only be reached from that computer's browser. Starting OpenBook opens it in your browser; starting it again while it runs just opens the browser again. It keeps running after you close the browser (so backups and other jobs can run), with an OpenBook icon in the system tray by the clock, until you quit it or sign out of Windows. Right-click the tray icon for **Open OpenBook**, **Back up now** and **Quit OpenBook**. Quitting, there or with **Settings → System → Quit OpenBook**, backs up your books first (to the same place as the daily backups; the last five of these are kept), so closing OpenBook always leaves a fresh copy. If that backup doesn't work, the tray asks before quitting anyway. When a new version of OpenBook is out, it's downloaded in the background and the tray offers **Install OpenBook x.y.z** (so does **Settings → System → Updates**, which also has **Check now**). Installing backs up your books, closes OpenBook, updates it and starts it again by itself within a minute or two. It's installed on Windows from `OpenBook-Setup.exe` (for your Windows user, no administrator rights) and started from the Start menu; installing a newer version or uninstalling never touches the company file or its backups.
- **OpenBook Server** runs on a server or in Docker, usually with PostgreSQL, for several people on several computers. Whoever runs it decides where the data lives and backs it up.

Whoever installs or looks after OpenBook will find the details in `docs/admin/desktop.md` and `docs/admin/server.md` (HTTPS, keys, `pg_dump` backups, moving a company in).

Your books are the same either way. Some things only make sense when others can reach OpenBook (invitations to people on other computers, customer portal links, sending from the capture app on a phone, Stripe telling OpenBook about payments); on Desktop those pages say so, and everything still works on the computer itself.

### Moving to a server

When more people need the books, the company moves from OpenBook Desktop to OpenBook Server in one step, usually done by whoever looks after the server:

1. Quit OpenBook Desktop (**Quit OpenBook** on its tray icon, or **Settings → System → Quit OpenBook**), so nothing changes during the move.
2. Create an empty PostgreSQL database for the server.
3. Run the `move-to-postgres` command with the company file, the new database and both keys folders (the server guide, `docs/admin/server.md`, has the exact command). OpenBook copies the company file, checks it like a restore, copies everything into PostgreSQL and checks again: every journal entry balances, every account has the same balance, and the audit log is complete. If anything differs, nothing is moved.
4. Start OpenBook Server on the new database and sign in as before: users, passwords, two-factor sign-in, email, bank and payment connections all come along, because stored passwords and keys are re-encrypted for the server.

The desktop company file isn't changed. Keep it as a backup, but stop using it: changes made there afterwards don't reach the server. The move is recorded in the server's audit log.

## The System page

**Settings → System** (admins) shows how the server is set up: the OpenBook version, the database and where the company file is, the backups and log folders, and the company's time zone. On OpenBook Desktop it shows the company file, backups and log folders instead, with **Quit OpenBook**. Below that:

- **Background jobs**: the jobs OpenBook runs on its own (recurring documents, late fees and reminders, scheduled emails and reports, bank feeds, sales channels, scheduled backups, webhooks), each with when it last ran, whether it worked and its last failure. If automatic invoices or bank feeds seem to have stopped, look here first.
- **Recent warnings and errors** since OpenBook last started.

OpenBook also writes a log file for each day (`openbook-yyyymmdd.log`) to the log folder, keeping two weeks. By default the folder is called `logs`, next to the company file or the backups folder; whoever runs the server can choose another with the `OpenBook:LogDirectory` setting.
