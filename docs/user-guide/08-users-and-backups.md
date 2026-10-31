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

- **How often**: daily or weekly, at an hour in the server's time.
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
