# Running OpenBook Server

For whoever looks after the server. OpenBook Server is the Docker image with PostgreSQL, for several people working on the books from their own computers. (One person on one Windows computer can use [OpenBook Desktop](desktop.md) instead; a company can move from Desktop to Server later, see [Moving a desktop company in](#moving-a-desktop-company-in).)

## Starting it

From the folder with `docker-compose.yml`:

1. Change both `change-me` passwords in `docker-compose.yml` (the database's and the one in the app's connection string) to the same long random password.
2. `docker compose up -d` builds the image and starts PostgreSQL and OpenBook. The first start creates the database tables.
3. Open `http://<server>:8080` and set up the company (or move a desktop company in first: the move needs an empty database).

`GET /healthz` answers `ok` while OpenBook runs and its database answers, for load balancers and monitoring. The image has a Docker `HEALTHCHECK` that asks it every 30 seconds (with `dotnet OpenBook.Web.dll healthcheck`, since the image has no curl), so `docker ps` and compose show the container as healthy or unhealthy; `healthcheck --url …` asks another address.

Data lives in two Docker volumes: `db-data` (PostgreSQL) and `app-data` (`/data` in the app: Data Protection keys, logs, the email folder). Both matter for a restore.

## HTTPS behind a reverse proxy

OpenBook itself speaks plain HTTP on port 8080. Anything beyond a trusted local network needs HTTPS, from a reverse proxy in front of it. The compose file sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, so OpenBook trusts the proxy's `X-Forwarded-For` and `X-Forwarded-Proto` headers: sign-in cookies are then marked secure and links use `https`. Because it trusts those headers from anyone, nothing but the proxy may reach port 8080: when the proxy runs on the same host, change the port line to `"127.0.0.1:8080:8080"`.

Pages update over a WebSocket (Blazor), so the proxy must pass WebSocket upgrades and keep idle connections open.

**Caddy** (gets and renews a certificate by itself), `Caddyfile`:

```
books.example.com {
    reverse_proxy localhost:8080
}
```

**nginx**, inside the `server { listen 443 ssl; … }` block for your name and certificate:

```
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_read_timeout 1h;
    client_max_body_size 100m;
}
```

with `map $http_upgrade $connection_upgrade { default upgrade; '' close; }` in the `http` block. `client_max_body_size` covers attachment and import uploads.

Once it's on HTTPS, use the public address wherever OpenBook asks for one: **Settings → Online payments → Public address of OpenBook** (customer portal links and Stripe's return pages) and the redirect address shown on **Settings → Sign-in** for single sign-on.

## Data Protection keys

OpenBook encrypts with ASP.NET Data Protection keys kept in `/data/keys` (the `OpenBook:DataProtectionKeys` setting; the image sets it). They protect:

- sign-in cookies (without the keys everyone simply signs in again), and
- secrets stored in the database: ACH files (they hold vendors' bank account numbers), the email (SMTP) password, two-factor sign-in secrets, Stripe, bank connection, sales channel and backup storage keys, webhook signing secrets, the single sign-on client secret, the Claude API key, and vendors' bank account numbers.

If the keys are lost, the database alone can't read those secrets: everyone with two-factor sign-in needs it reset (`reset-password <user> --clear-two-factor`, below) and the connections and passwords must be entered again. So back up `/data/keys` together with the database, and keep the backups as private as the keys themselves: anyone with both can read the secrets.

## Backups with pg_dump

The in-app backup buttons are for the single SQLite company file; with PostgreSQL the Backup page shows these commands instead. A backup is a `pg_dump` custom-format file plus a copy of the keys:

```
docker compose exec -T db pg_dump -U openbook -d openbook -Fc > openbook-$(date +%Y%m%d-%H%M).dump
docker compose cp openbook:/data/keys ./keys-backup
```

Schedule it, for example every night at 01:30 from the host's crontab, keeping 30 days:

```
30 1 * * * cd /srv/openbook && docker compose exec -T db pg_dump -U openbook -d openbook -Fc > backups/openbook-$(date +\%Y\%m\%d).dump && find backups -name 'openbook-*.dump' -mtime +30 -delete
```

and copy the `backups` folder somewhere else (another machine, object storage). `pg_dump` takes a consistent snapshot while OpenBook runs.

To restore, stop OpenBook, replace the database and start it again:

```
docker compose stop openbook
docker compose exec -T db dropdb -U openbook openbook
docker compose exec -T db createdb -U openbook openbook
docker compose exec -T db pg_restore -U openbook -d openbook --no-owner < openbook-20261101.dump
docker compose start openbook
```

If the keys were lost too, put the saved keys back into the `app-data` volume (`docker compose cp ./keys-backup/. openbook:/data/keys`) before starting OpenBook. Try a restore on a spare machine now and then: a backup is only proven by restoring it.

The ledger's safeguards (balanced, immutable entries; the append-only audit log) are database triggers, and `pg_dump` / `pg_restore` carry them along. OpenBook's restore checks don't run on a `pg_restore`, so restore only your own dumps.

## Updates

A server doesn't check for new versions unless `OpenBook:Updates:Check` is `true` (for example `OpenBook__Updates__Check: "true"` in the compose file). It then shows what's available on **Settings → System → Updates**, and never installs anything itself. Back up the database (above), then update it yourself:

```bash
docker compose pull
```

```bash
docker compose up -d
```

The database is migrated when the new version starts.

## Moving a desktop company in

A company kept by OpenBook Desktop moves into the server's empty database with the `move-to-postgres` command. It copies the company file (which stays as it was), checks it like a restore, copies everything in one transaction and checks again (every entry balances, every account's balance and the audit log match, the ledger triggers are there) before keeping anything. Stored secrets are re-encrypted with the server's keys, so two-factor sign-in and connections keep working.

1. On the desktop computer, quit OpenBook (**Quit OpenBook** on its tray icon, or **Settings → System → Quit OpenBook**) and copy two things to the server, say into `/srv/openbook/move`:
   - the company file `Documents\OpenBook\openbook.db`;
   - the keys folder `%LOCALAPPDATA%\OpenBook\keys`.
2. Make sure the server's database is empty: nobody has set up a company there. (If someone did, recreate it with `dropdb` / `createdb` as above.)
3. Run the move in a one-off app container, which uses the server's own keys folder as the target:

   ```
   docker compose run --rm -v /srv/openbook/move:/move openbook move-to-postgres \
     --from /move/openbook.db --source-keys /move/keys \
     --to "Host=db;Database=openbook;Username=openbook;Password=<the database password>"
   ```

   If the desktop file was encrypted, add `-e OpenBook__EncryptionKey=<its key>` after `run --rm`.
4. `docker compose up -d` (or restart `openbook`) and sign in as on the desktop. The move is the newest entry in **Settings → Audit log**.
5. Delete the copied keys from `/srv/openbook/move`. Keep the desktop file as a backup, but stop using it.

The command can also run on the desktop computer against the server's database, from the OpenBook program folder: `OpenBook.Web.exe move-to-postgres --to "Host=<server>;…" --target-keys <a copy of the server's keys folder>`. Whichever side it runs on, the other side's keys folder is the one to give.

Nothing is changed when a check fails; the message says what differed. `move.target_not_empty` means the database already has data; `move.secret_unreadable` means the source keys folder isn't the one the company was used with.

## Other tasks

- **A forgotten password**: `docker compose exec openbook dotnet OpenBook.Web.dll reset-password <username>` (add `--clear-two-factor` for a lost phone).
- **Updating OpenBook**: take a backup, then `docker compose build --pull && docker compose up -d`. New database changes are applied when OpenBook starts.
- **Logs**: one file per day in `/data/logs`; **Settings → System** shows background jobs and recent problems.
- **Offering the QuickBooks export helper**: copy `OpenBook.QuickBooksExport.exe` (built by `tools/package-qb-export.ps1`, see [the desktop guide](desktop.md#the-quickbooks-export-helper)) into a `QuickBooks` folder beside `OpenBook.Web.dll` (in a container, `/app/QuickBooks`) and the import page links to it for accountants.
- **Rate limits**: sign-in attempts (passwords, two-factor codes, single sign-on, accepting an invitation) are limited to 20 a minute per address, and the links people open without signing in (customer portal, payment pages, invitations, capture devices, Stripe's webhook) to 120. Too many answers 429 "Too many tries". Change them with `OpenBook__RateLimits__SignInPerMinute` and `OpenBook__RateLimits__LinksPerMinute` (0 turns one off). Behind a proxy the limits apply to the client's address only with forwarded headers on (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`, as the compose file sets); otherwise every request comes from the proxy and shares one limit. **Settings → System** shows the limits in force.
- **More than one OpenBook container** on the same PostgreSQL database (behind a load balancer): every container serves pages, and each background job (the hourly work, scheduled emails, webhooks, bank and channel syncs, backups) runs in one container at a time, which holds a lease on it and renews it each run. If that container stops, another takes the job over once the lease lapses (within 90 minutes for the hourly work, minutes for the rest). Share the Data Protection keys folder between them, or sign-ins and stored secrets won't work across containers.
- **SQLite on a server**: the image also runs without PostgreSQL (`docker run -p 8080:8080 -v openbook-data:/data openbook`): one company file in `/data`, with the in-app backups. PostgreSQL is better for several people.
