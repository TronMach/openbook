# People, backups and safety

## Users and roles

Administrators add people on **Settings → Users**. Each person signs in with their own username, so the audit log shows who did what.

**Add a user** asks for a username, a starting password (at least 8 characters; ask them to change it after signing in) and a role:

| Role | Can |
| --- | --- |
| View-only | See lists, documents and reports; change nothing |
| Standard | Also enter and void day-to-day work: invoices, payments, bills, expenses, bank imports and reconciling, time |
| Accountant | Also the books themselves: chart of accounts, journal entries and opening balances, imports, closing the books, most settings, the audit log, budgets, accountant reports |
| Admin | Also people and the system: users, email and online payment settings, webhooks, backups and restore, data export, add-ins |

Pages a role can't use are left out of its menu; trying to open one shows an "access denied" page.

From the Users page an administrator can change someone's role, **reset their password** or **make them inactive** when they leave. Changing a role or making someone inactive signs them out everywhere at once. People are never deleted, so their name stays on what they entered. There's always at least one active administrator, and you can't change your own role or deactivate yourself.

## Two-factor sign-in

Two-factor sign-in asks for a 6-digit code from an authenticator app (Google Authenticator, Microsoft Authenticator, 1Password, Authy and others) after the password, so someone who learns your password still can't get in.

**Turn it on:** select your username in the top bar, then **Two-factor sign-in: off, set it up** (or **Settings → Two-factor sign-in**). Scan the QR code with your app (or type the key shown under it), enter the code the app shows and select **Turn on two-factor sign-in**. OpenBook then shows ten **recovery codes**: save them somewhere safe (print them or keep them in a password manager). Each one signs you in once if you lose your phone; they aren't shown again.

**Signing in:** after your password, enter the code your app shows for OpenBook, or one of your recovery codes. Tick **Remember this browser for 30 days** on a computer or phone that's yours, to skip the code there. Changing your password or **Sign out all other sessions** forgets every remembered browser. Wrong codes count like wrong passwords: after five in a row the account is locked for five minutes.

On the same page you can **Make new recovery codes** (the old ones stop working) or **Turn off two-factor sign-in**; both ask for your password and a code. To move to a new phone, turn it off and set it up again.

**Lost your phone and your recovery codes?** An administrator can select **Turn off** in the **Two-factor** column on **Settings → Users**; that also signs you out everywhere. If no administrator can sign in, the `reset-password` command with `--clear-two-factor` does it (see [Forgotten password](01-getting-started.md#forgotten-password)).

**Requiring it (administrators):** **Settings → Sign-in and two-factor** → **Require it for**: nobody (each person decides), admins, accountants and admins, everyone but view-only users, or everyone. People in those roles who haven't set it up are taken to the setup page at their next sign-in and can't do anything else until they have; the page lists who that is. API keys keep working without a code.

### Inviting someone

To bring someone in, such as an outside accountant, invite them rather than making up a password for them: **Settings → Users** → **Invite someone**. Enter their email (and a name if you like), choose a role, and optionally **Access ends on**, for someone who needs the books for a while, like tax season. **Link works for** sets how long the invitation stays open (7 days unless you choose otherwise).

**Send invitation** emails them a link when email is set up (**Settings → Email**); either way the page shows the link once, to copy and send yourself. Following it, they choose their own username and password and are signed in with the role you gave them. If their role must use two-factor sign-in, they set it up next.

The **Invitations** list shows each one as waiting, accepted, expired or withdrawn. **Send again** makes a new link (the old one stops working); **Withdraw** cancels it. Each link works once.

### Email and end of access

Select a username on the Users page to set their **Email** and **Access ends on**. From the day access ends, they can't sign in, their API keys stop working, and within the hour they're made inactive (signed out everywhere). Clear the date to keep them on. You can't end your own access, and there must always be an administrator whose access doesn't end.

### Working at the same time

Several people can work in the books at once. If two of you save a change to the same thing at the same moment (say, both receive a payment on one invoice, both post the same bank line, or both sell the last units of a product), OpenBook usually tries yours again with the other change included, so it still checks for overpayments, stock that's run out and anything already done. If it still can't save yours, nothing of yours is saved and you see "Someone else saved a change to this at the same moment, so nothing was saved. Try again." Reload the page to see what changed, then enter it again if it's still needed.

## Closing the books

Once a period is finished (a month you've reconciled, a year your accountant has filed taxes for), close it so nobody changes it by accident: **Settings → Close the books**.

- **Close the books through**: the last date that's closed. Nothing dated on or before it can be entered, voided or changed. Leave it empty to reopen every period.
- **Closing date password** (optional, at least 6 characters, separate from sign-in passwords): with one, someone who knows it can still change a closed period by typing it when OpenBook asks. Without one, closed periods can't be changed at all until the closing date is moved. Every change made with the password is recorded in the audit log.
- Moving the closing date earlier, clearing it, or changing or removing the password needs the current closing password.

**Fiscal years** on the same page lists each year with its net income. Closing a year moves the closing date to its last day. No closing entries are posted: the balance sheet carries each past year's net income into retained earnings automatically.

## The audit log

**Settings → Audit log** lists every change to the books: who made it, when, and what changed, field by field. Filter by type of record, user and dates. The log can't be edited or deleted, even by an administrator. Most record pages have a **History** link that opens the log for just that record.

## Backups

If you use the single company file (not PostgreSQL), administrators back it up on the **Backup** page.

### Download a backup

Downloads a copy of the whole company file, attachments included, as a `.openbook.db` file. Keep copies somewhere other than the computer running OpenBook.

### Scheduled backups

Set OpenBook to back itself up:

- **How often**: daily or weekly, at an hour in the company's time zone (**Settings → Time zone**; the server's if none is set).
- **Keep the newest**: how many to keep; older scheduled backups are deleted.
- **Save to**: a **folder** (give the full path, ideally a different drive or a synced folder), or **S3-compatible storage** such as Amazon S3, Backblaze B2, Cloudflare R2, Wasabi or MinIO (service URL, region, bucket, folder and an access key that can only put, list and delete in that bucket; the secret is stored encrypted).

**Save schedule**, then **Back up now** to check it works. The page shows the outcome of the last run. The first scheduled backup runs right after you save.

### Restoring a backup

**Restore from a backup**: choose the backup file, type your password and select **Restore backup**. OpenBook checks the file first (that it's an OpenBook company file, undamaged, and balances) and refuses it otherwise. Before replacing anything it saves a safety copy of the current books in a `backups` folder next to the company file, so a restore can itself be undone. Everyone else is signed out.

### With PostgreSQL

The Backup page's buttons only work with the single company file. Back up a PostgreSQL database with your usual database tools, such as `pg_dump`.

### Encrypted company files

The company file can be encrypted, so a stolen copy can't be read without the key. This is set up by whoever runs the OpenBook server (the `OpenBook:EncryptionKey` setting). Backups of an encrypted file are encrypted with the same key: keep the key safe, because without it neither the file nor its backups can be opened.

## Exporting all your data

**Backup → Export all data** downloads a ZIP with every table as a CSV file, every attachment, and a README describing each file and column. It's for taking your data elsewhere or analysing it in a spreadsheet; it isn't a backup you can restore. Passwords, keys and other secrets are left out, as are vendors' tax IDs.

## The System page

**Settings → System** (admins) shows how the server is set up: the OpenBook version, the database and where the company file is, the backups and log folders, and the company's time zone. Below that:

- **Background jobs**: the jobs OpenBook runs on its own (recurring documents, late fees and reminders, scheduled emails and reports, bank feeds, sales channels, scheduled backups, webhooks), each with when it last ran, whether it worked and its last failure. If automatic invoices or bank feeds seem to have stopped, look here first.
- **Recent warnings and errors** since OpenBook last started.

OpenBook also writes a log file for each day (`openbook-yyyymmdd.log`) to the log folder, keeping two weeks. By default the folder is called `logs`, next to the company file or the backups folder; whoever runs the server can choose another with the `OpenBook:LogDirectory` setting.
