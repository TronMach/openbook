# Threat model

For admins deciding how to run OpenBook, and for security reviewers. It says what OpenBook protects, from whom, what it does about each threat, what it leaves to you, and what it can't protect against. How to report a problem: [SECURITY.md](../../SECURITY.md). Running it: [the server guide](server.md) and [the desktop guide](desktop.md).

## What OpenBook protects

- **The books and their history.** Journal entries, documents (invoices, bills, payments …), the closing date, and the audit log of every change with who made it.
- **Personal data.** Customers', vendors' and users' contact details; vendors' tax IDs and bank account numbers (ACH); attached files.
- **Secrets.** Users' password hashes, two-factor secrets and recovery codes, API keys, invitation and link tokens, and the credentials OpenBook keeps for other services: SMTP password, Stripe keys and webhook secret, SimpleFIN and bank connections, sales channel tokens, S3 backup keys, webhook signing secrets, the single sign-on client secret, the Claude API key. Outside the database: the ASP.NET **Data Protection keys** (which encrypt most of those secrets and the sign-in cookies), the **SQLCipher key** of an encrypted company file, the PostgreSQL password, and the maintainers' **update release key**.
- **Availability.** People being able to sign in and work, and the books surviving a lost disk.
- **Backups.** A backup holds everything above that's in the database.

## Who might attack, and from where

- someone on the internet, at a Server's public address (sign-in, anonymous links, the API);
- a customer, or whoever has their link: the portal (`/portal/…`), document links (`/view/…`), payment pages, invitations;
- a signed-in user trying to do more than their role or permission set allows;
- a program with a stolen or limited API key;
- a malicious file: attachments, statements and OFX, migration files, CSVs, documents to read, fonts, backups to restore;
- a malicious or compromised service answering OpenBook: SimpleFIN, Stripe, sales channels, Claude, Google Fonts, the update feed;
- someone with the laptop, the disk or a backup;
- someone with the server, Docker, the database login or the company file;
- a malicious add-in, loaded into OpenBook itself;
- a fake update offered to the desktop.

## Threats and what OpenBook does

### Someone on the internet (Server)

What OpenBook does:

- **Every page and endpoint needs sign-in** unless marked public; the API answers 401 rather than redirecting. Public pages are the sign-in pages, `/setup` (only until a company exists), the anonymous links below, `/healthz` and the OpenAPI description.
- **Password guessing**: per-address rate limits (`RateLimits`: 20 sign-in posts a minute, covering passwords, two-factor codes, single sign-on and invitations; 120 a minute to anonymous links; then 429). After 5 wrong passwords one address is shut out of that account for 5 minutes (`SignInThrottle`) while the owner can still sign in elsewhere; after 100 from all addresses the account itself is shut. Two-factor codes lock after 5, and count against the password too. The sign-in page doesn't say which usernames exist.
- **Passwords** are hashed with ASP.NET Core Identity's PBKDF2 hasher, 8 to 128 characters.
- **Two-factor sign-in** (TOTP; recovery codes stored as SHA-256 hashes; no code accepted twice) can be required from a role upwards. Single sign-on (one OpenID Connect provider, code flow with PKCE) can be required for all but admins.
- **Cookies**: `OpenBook.Auth` is HttpOnly, SameSite=Lax, and secure on HTTPS (behind a proxy, with forwarded headers on). Its security stamp and session id are checked on every request, so a password, role or permission set change, deactivation, "Sign out all other sessions" or a restore ends sessions. Sessions end after 8 hours unused; people see and end theirs on `/account/password`.
- **Headers** (`SecurityHeadersMiddleware`): `nosniff`, no framing by other sites, `Referrer-Policy: same-origin` (link tokens don't leak); HSTS outside development. No full content security policy: the pages use inline script.
- **Cross-site requests**: every signed-in form post checks an antiforgery token (a test walks every route), on top of SameSite cookies.

Left to you: HTTPS from a reverse proxy, with nothing but the proxy able to reach port 8080 (OpenBook trusts `X-Forwarded-*` from whoever sends them when forwarded headers are on); a firewall; requiring two-factor sign-in for admins and accountants; setting up the company as soon as a new server starts, since until then anyone who reaches `/setup` becomes its owner.

### A customer, or whoever has their link

Portal, document, payment and invitation links are bearer links: whoever has the address gets what it shows.

What OpenBook does: tokens are 192 or 256 random bits, stored only as SHA-256 hashes; a link shows one customer's own documents (or one invitation); links can be revoked and invitations expire (7 days by default); these paths are rate-limited per address. A payment is recorded only from Stripe's signed webhook (HMAC-SHA256, calls older than 5 minutes refused, compared in constant time) or by OpenBook asking Stripe about the checkout itself, never from what the browser says. Answering an estimate is an antiforgery-checked form.

Left to you: sending links only to the right people, and revoking a portal link when a customer relationship ends (portal links don't expire).

### A signed-in user going beyond their role

What OpenBook does:

- **One access decision** for pages, form posts and the API: `AccessRules` by path and method, enforced by `AccessMiddleware` on every request. Roles are View-only < Standard < Accountant < Admin; a **permission set** gives a level per area instead. Tests check every page has an area and the rules' mapping. Buttons a person can't use are greyed out, but the server decides.
- Users, others' API keys, webhooks, sign-in settings, backups, restore, the data export and add-ins are admin only. There's always an active admin; nobody changes their own role or access.
- **Closed periods**: posting or voiding before the closing date needs the closing password (stored hashed), and each override is audited.
- **Posted entries are immutable.** Everything that affects balances posts through `PostingEngine`; corrections are voids posting a reversal. Database triggers refuse an unbalanced entry, any change to a sealed entry or its lines, and any update or delete of the audit log (`SqliteLedgerTriggers`; the same in PL/pgSQL on PostgreSQL, which also refuses TRUNCATE).
- **Every change writes an audit record** in the same save, naming the user.

Left to you: giving people the lowest role that lets them work, ending access (users can be given an end date), and looking at the audit log.

### A stolen or limited API key

What OpenBook does: keys (`obk_…`, 256 random bits) are shown once and stored as SHA-256 hashes. They act as their user, with that user's role or permission set, and stop when the user is deactivated or their access ends. Keys can expire and be revoked, by their owner or an admin; last use is recorded. A **scoped key** ("area:read" / "area:write") is refused outside its areas before the role check and never reaches users, keys, webhooks, sign-in or email settings, or pages. Retries with an `Idempotency-Key` never store a response holding a one-time secret.

Left to you: scoping keys, giving them an expiry, keeping them out of scripts and repositories, revoking one that may have leaked. API keys skip two-factor sign-in by design.

### A malicious uploaded or imported file

What OpenBook does:

- Attachments must be one of a fixed list of types (PDF, images, text, CSV, Office files, email) up to 10 MB. They're served with `nosniff`, a sandboxing content security policy, and as a download unless they're an image or PDF. OpenBook never runs an uploaded file.
- Every reader of files and service answers (migration files, OFX, CSV, statements, channel reports, document text, Claude's answers, SimpleFIN, update manifests, report queries, …) must read the input or refuse it with an ordinary error. They're **fuzzed** with damaged samples on every test run, each within 5 seconds (`tests/OpenBook.Integration.Tests/Fuzz`). XML is read with DTDs prohibited and no resolver, so no external entities. Amounts read stop at `Money.MaxAmount`, so sums can't overflow.
- Imports preview first and go in all at once or not at all, through the same services and rules as typing.
- Fonts are checked before use. A backup is restored only after checks (integrity, known migrations, ledger triggers present, the ledger balanced) on a migrated temporary copy, with a safety copy of the current books kept.

Left to you: if you set up Tesseract for reading photos, keep it updated, since it reads uploaded images. Restore only your own backups and `pg_dump` files: a PostgreSQL restore doesn't go through OpenBook's checks.

### A malicious or compromised third-party service

What OpenBook does:

- **Outbound calls to addresses people type in** (webhooks, SimpleFIN claim and access URLs) go through `PublicNetwork.Handler`: it resolves the name and refuses loopback, private, link-local (cloud metadata), shared, multicast and reserved addresses, connecting to the address it checked, and follows no redirects. `OpenBook:Outbound:AllowPrivateNetworks=true` turns this off. Shopify addresses must end in `.myshopify.com` and are https only.
- Answers go through the fuzzed readers above. Bank feed lines wait for review unless a rule you made adds them; documents read by Claude become drafts a person checks. Google Fonts files come only from `fonts.gstatic.com` over https and are checked as fonts.

Left to you: what you send. Claude reading sends those documents to Anthropic; bank, Stripe and channel connections exchange data with those companies; webhooks send audit snapshots (customer and vendor details, never secrets) to the address you give. S3 backup and SMTP hosts may be on your network; choose TLS for SMTP.

### Someone with the laptop, the disk or a backup file

What OpenBook does:

- Secrets in the database are encrypted with Data Protection (`ISecretProtector`, columns named `Protected…`), so the company file or a backup alone doesn't give them up. Password hashes, API key and token hashes, protected secrets, vendor tax IDs, ACH files and attachment bytes are left out of the data export (`DataExporter.Excluded`).
- **SQLCipher** can encrypt the SQLite company file (`encrypt-database`, key `OpenBook:EncryptionKey`); backups made afterwards use the same key, and restore accepts only backups with it.
- Offline capture drafts on a phone are encrypted in the browser's storage with a non-extractable key. That keeps them out of the browser's files as plain text; anyone who can use that browser can use the key too. A revoked device wipes its drafts the next time it checks.

Left to you: the Data Protection keys folder is **not encrypted** (Desktop `%LOCALAPPDATA%\OpenBook\keys`, Docker `/data/keys`) so it can move with the company; anyone with the keys and a database or backup can read every protected secret, so keep the keys as private as the backups. Use disk encryption (BitLocker, LUKS): a SQLCipher key kept in an environment variable on the same disk protects nothing if the disk is taken. Keep backups somewhere else: Desktop's daily backups go to `Documents\OpenBook\Backups` on the same disk, and a Documents folder synced to OneDrive or similar takes the company file and backups there. Backups made before encrypting stay unencrypted.

### Someone with the server or the database

What OpenBook does: makes changes visible afterwards. Each audit record is chained to the one before by a SHA-256 hash (`AuditChain`, append-only by trigger). The books check (daily, and on `/accountant/check`) works the chain out again, along with whether entries balance and documents agree with the ledger. The chain's position and hash are written to the log once a day and into the accountant's package; `verify-audit --fingerprint <position>:<hash>` checks that a fingerprint noted then is still the same, and `--against <backup file>` compares with a SQLite backup.

Left to you: keep a fingerprint somewhere the server can't reach (the accountant's package, a note, an emailed log line), and run `verify-audit` now and then. Protect shell, Docker and database access like the books themselves. In the compose file, OpenBook connects as `openbook`, the database's owner (the PostgreSQL image makes it a superuser); keep its password long and the database port unpublished, as the compose file does.

### A malicious add-in

What OpenBook does: add-ins are DLLs in the plugins folder, loaded at startup. They can only be installed by someone who can write to that folder on the machine; there is no upload. Admins see each one on `/settings/plugins` and can turn it off (applied at restart); `--safe-mode` or `OpenBook:Plugins:SafeMode=true` starts without any.

Left to you: an add-in can do anything OpenBook can (below), so install only ones you trust and let only admins write to the plugins folder.

### A fake update

What OpenBook does: each release's `latest.json` names the version, the installer's address, size and SHA-256, and is signed with the release key (ECDSA P-256). Copies trust only the public key built into them (`UpdateKeys`); they ignore an unsigned or wrongly signed manifest and throw away a download whose size or hash doesn't match. Only a higher version is offered. The feed must be https (plain http only on the same computer). Desktop backs up before installing. A server never installs anything; it only reports when `OpenBook:Updates:Check` is on.

Left to you (maintainers): the release key is kept outside the repository and as a GitHub Actions secret; whoever holds it can sign an update every desktop copy will install.

## What OpenBook doesn't protect against

- **Whoever controls the server, the database or the company file can change anything.** Triggers stop mistakes and OpenBook's own code, not someone who can drop them; the database login OpenBook uses can, and so can anyone with the SQLite file. `reset-password --restore-admin` gives anyone with a shell an admin account. The audit chain only shows a change afterwards, and only against a fingerprint or backup kept elsewhere: someone with the database, every log file and every backup could rewrite them all, and a record inserted with a new id and a believable time looks like any other.
- **An admin user** can add users, raise roles, create keys and webhooks, export all data, download and restore backups, and change settings. All of it is audited, but an admin isn't restricted.
- **An add-in has full trust.** It runs in OpenBook's process with OpenBook's access to the database, the keys and the network. Its own load context only keeps its dependencies apart; it isn't a sandbox.
- **A compromised client computer or browser.** Malware or a malicious browser extension can act as the signed-in person, read what's on screen and capture passwords and codes. On Desktop, anything running as the Windows user can also change the program itself (it's installed per user under `%LOCALAPPDATA%\Programs`) and read the keys folder.
- **The installer isn't code-signed yet**, nor the QuickBooks export helper. A first install downloaded from the web can't be told apart from a fake by Windows; get it from the project's GitHub releases. Updates the desktop downloads itself are checked by the signed manifest.
- **Denial of service.** Rate limits cover only sign-in and anonymous links, with counters in memory per process (reset on restart, not shared between containers). Guessing from many addresses can shut an account for everyone (100 failures, by design); signed-in users and API keys aren't limited. Add limits at the proxy if the server faces the internet.
- **Leaked bearer links.** A forwarded portal or document link works for whoever has it until it's revoked.
- **Compression at the proxy.** Compressing pages over HTTPS is what the BREACH attack needs; OpenBook leaves it off on purpose ([server guide](server.md#https-behind-a-reverse-proxy)).

## Desktop and Server differ

| | Desktop | Server |
|---|---|---|
| Listens on | `localhost` only, whatever the URL settings say (`DesktopHost`: Kestrel's `ListenLocalhost`), port 5180 or a free one | Plain HTTP on 8080; the compose file publishes it to `127.0.0.1` for a proxy |
| Who can reach it | Programs on this computer, other Windows users of it included (they still need an OpenBook password) | Whoever the proxy and firewall let through |
| Database | SQLite, optionally SQLCipher | PostgreSQL (or SQLite) |
| Backups | Daily from the start (14 kept), on quit and before updates | `pg_dump`, scheduled by you |
| Updates | Downloads, verifies, installs on request | Reports only, if turned on |
| Other | The tray's named pipe opens only to the same Windows user | `/healthz` for probes |

Customer links, invitations, capture devices and Stripe webhooks need others to reach OpenBook, which Desktop doesn't offer; its pages say so. Forwarding or tunnelling Desktop's port makes it an internet-facing server without HTTPS: use Server instead.

## Checklist for running it safely

Server ([server.md](server.md)):

1. Change both `change-me` passwords in `docker-compose.yml` before the first start, and set up the company straight away.
2. Put an HTTPS reverse proxy in front, and let nothing else reach port 8080 ([HTTPS behind a reverse proxy](server.md#https-behind-a-reverse-proxy)).
3. Back up the database **and** `/data/keys`, keep copies off the server, and try a restore now and then ([Backups with pg_dump](server.md#backups-with-pg_dump), [Data Protection keys](server.md#data-protection-keys)).
4. Require two-factor sign-in for admins and accountants (Settings → Sign-in); give everyone the lowest role that works.
5. Scope API keys and give them an expiry; revoke unused ones.
6. Keep the host, Docker and PostgreSQL updated, and update OpenBook when a release appears ([Updates](server.md#updates)).
7. Install add-ins only from people you trust; know `--safe-mode`.
8. Note the audit chain fingerprint somewhere off the server and check it with `verify-audit` ([Other tasks](server.md#other-tasks)).

Desktop ([desktop.md](desktop.md)):

1. Install from the project's GitHub releases; let it update itself ([Updates](desktop.md#updates)).
2. Turn on BitLocker (or another disk encryption) and lock the computer when you leave it.
3. Copy backups and `%LOCALAPPDATA%\OpenBook\keys` somewhere else, kept privately ([Moving to a new computer](desktop.md#moving-to-a-new-computer)).
4. Consider `encrypt-database`, which protects the file and backups once copied elsewhere; the key itself is only as safe as the disk ([Where things are](desktop.md#where-things-are)).
5. Don't expose its port to the network.
