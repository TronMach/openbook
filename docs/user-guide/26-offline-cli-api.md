# Offline capture, the command line, the API and add-ins

## Capturing work on a phone, even offline

The **capture page** is for entering things away from your desk, with or without a connection: on a job site, in the car, on a plane. Open it from **Time → Capture on a phone**, or go to `/capture.html` on your OpenBook address, sign in once, and add it to your phone's home screen.

It records:

- **Time**: person, date, hours, customer or project, service, notes.
- **Mileage**: date, miles, business purpose, customer or project, bill per mile.
- **Expense**: amount, paid from, category, vendor or payee, memo, and an optional receipt photo.
- **Receipt**: just a photo; it goes to the receipts inbox ([chapter 16](16-document-inbox.md#receipts)) to be read and recorded.
- **Invoice** or **estimate**: customer and lines (product or service, quantity, rate, description) and a message. Leave the rate blank to use the automatic price.

Entries wait on the device (encrypted) under **On this device**, and are sent when there's a connection: on opening, when the connection returns, every minute, or with **Send now**. OpenBook then enters each one by the same rules as its own forms, numbering invoices and working out tax as it does. Until then an invoice shows as a draft.

The capture page keeps a copy of your customer, service, account and vendor lists for the forms, but never balances.

### When something can't be entered

On OpenBook's **Capture** page (`/capture`), entries that need attention are listed with the reason, for example a closed period or an inactive customer. Fix the cause, then **Try again**, or **Dismiss** it.

### Lost phones

The same page lists every device that has used the capture page. **Remove access** for a lost or replaced one: it can't send anything more, it signs out the session it signed in with, and next time it connects it wipes its stored entries. Tick the box to also sign out all your other sessions. Changing your password is wise too.

## API keys

OpenBook has a REST API for connecting other programs and writing your own tools. Anything you can do on the pages, the API can do.

**Settings → API keys**, or the API keys link on your password page (select your username in the top bar). Anyone can make keys for themselves, whatever their role:

1. **Create a key**: give it a name, an optional expiry date, and choose **what the key can do**: everything your role allows, or only reading and/or changing some areas (ledger, sales, purchases, documents, banking, inventory, projects, channels, assets, nonprofit, reports, capture, data).
2. The key (starting `obk_`) is **shown once**. Copy it into the program that needs it.

A key acts as you, with your role, and never does more than your role allows. A limited key can't reach users, keys or webhooks at all. **Revoke** a key you no longer use; its last use is shown in the list.

Programs send the key as `Authorization: Bearer obk_…`. The **REST API** link on the Settings page opens the OpenAPI description of every endpoint, for developers.

To retry a write safely after a timeout, send an `Idempotency-Key` header (any unique text up to 100 characters, such as a GUID) with POST, PUT or DELETE. The first request with a key is done and its answer kept for a day; sending the same request with the same key again gets that answer back (with `Idempotency-Replayed: true`) instead of, say, a second invoice. The same key with a different request is refused (422). The command-line tool takes `--idempotency-key`.

Lists of invoices, payments, sales receipts, bills and bill payments return everything by default. Add `?take=100` (and `&skip=100` for the next page) to get the newest a page at a time, at most 500 per page; the `X-Total-Count` header says how many there are in all.

## Webhooks

Administrators can have OpenBook tell another system whenever something changes, for example to update a CRM when an invoice is paid. **Settings → Webhooks → Add webhook**: the **address** to call and the **events**, comma-separated (`*` for everything, or patterns like `invoice.*`, `*.voided`, `payment.created`). Each call is signed with a secret so the receiver can check it came from OpenBook. Failed calls are retried for several hours; the delivery log shows each attempt, with **Send again** and **Send a test event**.

Webhook addresses must be on the internet: OpenBook won't call addresses on the server's own network (such as `localhost`, `192.168.…` or `10.…`), so a webhook can't be used to reach machines that aren't meant to be public. If you do want to call a system on your own network, whoever runs the OpenBook server can allow it with the `OpenBook:Outbound:AllowPrivateNetworks` setting.

## The command-line tool

The `openbook` command talks to a running OpenBook server through the API, which is handy for scripts and scheduled jobs. From the OpenBook folder:

```bash
dotnet run --project src/OpenBook.Cli -- help
```

Set it up once with the server's address and an API key:

```bash
openbook config --url https://books.example.com --key obk_yourkey
```

Then, for example:

- `openbook whoami`: who the key belongs to.
- `openbook list customers`: a list as a table (`--json` for raw data).
- `openbook report profit-and-loss --query "preset=LastMonth" --format pdf --out pnl.pdf`: any report, as a table, JSON, CSV, Excel or PDF.
- `openbook import iif books.iif --dry-run`: preview an import (sources: iif, qbxml, qbo, gnucash, xero, wave, freshbooks) ([chapter 24](24-moving-in.md)).
- `openbook export --out data.zip` and `openbook backup --out books.openbook.db`.
- `openbook check` checks that the books agree with themselves (accountant role; see [Checking the books](20-budgets-and-analysis.md#checking-the-books)) and exits with code 3 when something doesn't.
- `openbook get /sales/invoices`: any API call.

Add `--closing-password` when a change falls in a closed period. The command exits with 0 when it worked, 1 when the server refused or couldn't be reached, and 2 for a mistake in the command.

## Add-ins

Add-ins extend OpenBook with extra reports and import formats. Whoever runs the server installs one by putting it in its own folder under `plugins` next to the app and restarting.

**Settings → Add-ins** (administrators) lists each folder, its version, whether it loaded, and what it adds. **Turn off** or **Turn on** applies at the next restart. If an add-in stops OpenBook from starting, start it in safe mode (`--safe-mode`), which loads none.

Only install add-ins you trust: they run inside OpenBook with full access to your books.
