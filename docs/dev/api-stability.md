# API, webhook and add-in stability

For people building on OpenBook: scripts and integrations using the API, webhook receivers, the `openbook` command-line tool, and add-ins. It says what you can rely on from one release to the next, what may change, and how changes are announced.

OpenBook is before 1.0 (the latest release is 0.16.0). The policy below already applies; what changes at 1.0 is only where a breaking change may land (see "Rules for changes").

## What's covered

- **API routes under `/api`**: their paths, methods, query parameters, request and response JSON fields, status codes, and the headers OpenBook documents (`Authorization: Bearer obk_…`, `X-Closing-Password`, `Idempotency-Key` and `Idempotency-Replayed`, `ETag` / `If-Match`, `X-Total-Count`, `?skip=` / `?take=`).
- **Error codes.** Every refusal from `/api` has a JSON body with a `code` such as `invoice.already_voided`, `save.stale`, `auth.scope` or `request.value_missing`. Domain errors are problem details (`title` holds the message, `code` is an extension); a few refusals made outside the domain (scopes, rate limits, some upload checks) are `{ "code", "message" }`. Rely on the status and the `code`; the message text is for people and may be reworded in any release.
- **Webhook event names and payload shape**: the envelope (`id`, `type`, `occurredAt`, `actor`, `entity.type`, `entity.id`, `before`, `after`), the `OpenBook-Event`, `OpenBook-Delivery` and `OpenBook-Signature` headers, and the signature scheme (`t=…,v1=HMAC-SHA256(secret, "t.body")`). Event names are `{entity_type}.{action}` (`invoice.created`, `journal_entry.voided`), plus `ping` for a test event, which has no `entity`, `before` or `after`.
- **API key scopes**: the area names (`ledger`, `sales`, `purchases`, `documents`, `banking`, `inventory`, `projects`, `channels`, `assets`, `nonprofit`, `reports`, `capture`, `data`), the `area:read` / `area:write` form, and which routes each area reaches.
- **The CLI**: command names, options, the `--json` output (which is the API's JSON), the `OPENBOOK_URL` / `OPENBOOK_API_KEY` variables and the exit codes (0 worked, 1 server refused or unreachable, 2 a mistake in the command, 3 `check` found problems).
- **Report keys** as `/api/reports/{key}` and `openbook report <key>` take them, and import source keys (`iif`, `qbxml`, …).
- **The add-in contract** (its own section below).

## What isn't

- HTML pages, their URLs, and the form posts behind them (anything outside `/api`). Use the API instead of scraping pages or posting their forms.
- The database schema, the company file and backup files, and any file format other than a documented export. Read and write the books through the API.
- C# types other than the add-in contract: services, entities, the API's request and response records as types.
- The OpenAPI document's wording, ordering, operation ids and schema names. Its routes and fields follow the API; the rest is generated.
- Report layouts (columns, rows, headings, the look of PDFs and spreadsheets), message text, and the CLI's table output (use `--json` in scripts).
- Limits and timings (rate limits, page and upload sizes, retry schedules); the codes they answer with, such as `rate.limited`, are covered.

## How the API behaves today

Things a client can rely on, checked against the code:

- **Unknown fields in a request are ignored**, not refused (ASP.NET Core's JSON defaults). So a misspelt field is silently ignored too, and an unknown field may start to mean something in a later release: send only documented fields.
- JSON field names are camelCase; request field names are matched without regard to case. Enums travel as their names (`"Cash"`, `"Paid"`).
- **There is no version in the routes and no version header.** `v1` in `/api/openapi/v1.json` names the document, not a separate API. The API's version is OpenBook's version. No API call reports the server's version yet; an admin sees it on **Settings → System**.
- Writes to records listed in `ApiVersions.Routes` (documents, customers, vendors, items) check `If-Match` when it's sent and answer 412 `save.stale` when the record changed; without `If-Match` nothing is compared. Treat the `ETag` value as opaque.

What a client must do so that additive changes never break it:

- Ignore response fields it doesn't know, and expect new values in enum-like fields (statuses, types).
- Ignore webhook events it doesn't know. Subscribing to `*` or a pattern such as `invoice.*` will bring new events as OpenBook audits new things.
- Treat an unknown error `code` by its HTTP status.
- Expect webhook deliveries more than once and out of order: deduplicate on `id` (also in `OpenBook-Delivery`) and order by `occurredAt`.

## Rules for changes

**Additive changes may come in any release**, patches included: new routes, new optional request fields, new response fields, new error codes, new webhook events, new scope areas, new CLI commands and options, new enum values in responses, new add-in slots.

**Breaking changes** include removing or renaming a route, field, error code, event, scope area, CLI command or option; changing a field's type or meaning; making an optional field required or changing its default (for example paging a list that today returns everything); answering a case with a different status or code; and tightening a scope so a key reaches less than it did.

A breaking change:

1. is announced in the release notes at least one minor release ahead, saying what changes, what to use instead and in which release the old form goes;
2. keeps the old form working alongside the new for that release where practical (an old route answering as before, a renamed field sent under both names);
3. while OpenBook is 0.x, may land in a minor release (0.y.0), never in a patch (0.y.z); from 1.0, only in a major release.

Webhook payloads are fixed when a delivery is created, so a delivery retried after an upgrade still has the shape it had before.

**The exception: security and correctness.** A fix that stops a request doing something it never should (reaching data outside a key's scope, breaking a ledger rule) may tighten what's accepted in any release, without the notice period. The release notes say so.

Webhook `before` / `after` snapshots are the audit trail's record of the change. Their fields are covered on the same terms as API fields (added freely, removed or renamed only by the rules above), but they differ by entity, leave out secrets, and aren't documented field by field yet. Read them defensively, and use the API for the current state of a record.

## The add-in contract

Add-ins compile against `OpenBook.Application` (and through it `OpenBook.Domain`). At run time they use the OpenBook assemblies of the server they're loaded into, not the copies they were built with, so what matters is binary compatibility with that server.

The contract is:

- `IOpenBookPlugin`, `IPluginRegistry` and `PluginApi` (`src/OpenBook.Application/Plugins/Plugins.cs`);
- the types in those signatures: `ReportDefinition`, `ReportParameters`, `ReportTable` and its rows and columns, `MigrationData` and the records it holds;
- `DomainException` and the codes the registry throws (`plugin.key_invalid`, `plugin.key_taken`).

Not covered: the services an add-in resolves from the `IServiceProvider` its report receives (the pages' own services, which may change in any minor release: test an add-in that uses them against each one), everything else in those assemblies, and the host's `PluginRegistry`, `PluginInfo` and `PluginState`.

### The add-in API version

- `PluginApi.Version` (an `int`, currently 1) is the contract version this OpenBook offers. `PluginApi.Oldest` (currently 1) is the oldest it still loads.
- `IOpenBookPlugin.ApiVersion` says which version an add-in was written for. Its default implementation returns 1, so an add-in that doesn't say was written for version 1. A new add-in should return `PluginApi.Version`: it's a constant, so it's compiled into the add-in and records the version it was built against.

```csharp
public sealed class MyAddIn : IOpenBookPlugin
{
    public string Name => "My add-in";
    public string Version => "1.0.0";              // the add-in's own version
    public int ApiVersion => PluginApi.Version;    // the contract it was built against
    public void Register(IPluginRegistry registry) { /* AddReport, AddImporter */ }
}
```

- At startup OpenBook doesn't load an add-in whose `ApiVersion` is above its own `PluginApi.Version` (it needs a newer OpenBook) or below `PluginApi.Oldest` (this OpenBook no longer supports it). **Settings → Add-ins** shows it as "Failed to load" with the reason. An add-in that doesn't say, but calls something this OpenBook doesn't have, fails the same way, its reason saying it was built for a different version of OpenBook.
- `PluginApi.Version` goes up when the contract grows: a new slot on `IPluginRegistry`, or a new member of a contract type an add-in may call. An add-in using the new part returns the new version and so needs the new OpenBook; older add-ins keep loading.
- `PluginApi.Oldest` goes up only when something an older add-in may use is removed or changed incompatibly, announced a minor release ahead, and only in a minor release (from 1.0, a major one).

For contributors changing the contract: add members, don't change them. A new member on `IOpenBookPlugin` needs a default implementation (as `Description` and `ApiVersion` have). Adding to `IPluginRegistry` is safe, since add-ins call it rather than implement it. Adding a parameter to an existing method or record constructor, even with a default value, changes its signature and breaks add-ins already built (`MissingMethodException`): keep the old signature as an overload, or it counts as an incompatible change and raises `Oldest`.

On OpenBook Desktop, add-ins live outside the program folder and survive updates, so an update that raises `Oldest` turns off older add-ins until they're rebuilt.

## How changes are announced

- **GitHub release notes** ([Releases](https://github.com/TronMach/openbook/releases)). The release workflow (`.github/workflows/release.yml`) creates each release with GitHub's generated notes; whoever releases adds a **Breaking changes** section (and **Deprecated**, for what goes in a later release) by hand when there's anything to say. There's no deprecation header in API responses; the release notes are where deprecations are announced.
- **The change log below**, which lists every change to this policy, every breaking change and deprecation with the release it lands in, and each new add-in API version.

Contributors: a change to anything under "What's covered" that isn't additive needs an entry here and a line for the release notes in the same commit. Watch for the quiet ones: renaming a property of an API request or response record renames a JSON field; renaming an audit entity type or `AuditAction` member renames webhook events; renaming an enum member renames a value; changing a `DomainException` code string or an `ApiScopes.Areas` key breaks clients.

## Change log

- **2026-11-24** (second audit; behaviour changes for cases that were wrong):
  - Access: `POST /api/projects/time/reprice` needs the accountant role; `GET /api/attachments` and `/{id}/content` answer 403 to a limited key without a scope in the record's area; `GET /api/banking/accounts/{id}/feed/documents` leaves out what the caller may not write and `POST /api/banking/feed/{id}/pay-document` answers 403 without that area; posting billing-run drafts and billing milestones also needs the sales area.
  - A `PUT` keeps what's left out (null clears) for customers (`creditLimit`, `creditHold`, `exemptionStartsOn`, `exemptionRegions`, the first two now returned), tax agencies (`region`, `dueDays`, now returned), deposits (`extras`), channels (class, location, warehouses, `marketplaceCollectsTax`, now returned), and projects, workers, assets and loans (`arrangement`, `fee`, `classId`, `firstYearAmount`, `creditLimit`, `factorFee`, now returned). Unreadable JSON answers 400 `request.unreadable`.
  - Invoices and receipts may total zero when a gift card pays for all of it. Tax rates return `percentToday` and `changes`. Square webhooks of a part payment answer `online.not_paid_yet` until the order is paid. `online.links_open` also covers gifts under way and a switched sandbox.
  - Vendor deposits: `refunded` and `refunds`, `POST /api/purchases/vendor-deposits/{id}/refunds` and `.../refunds/{refundId}/void`; `CheckKind.VendorDeposit`. Feed items have `possibleDuplicate`; suggestions and bank rules `splits`. The `1099-detail` report gains the column "Not reportable".
  - Gifts return `returnedOn`; nonprofit settings `onlineGiving`. An advance's loan schedule includes its fee. `POST /api/inventory/items/file` returns `problemCode`.
  - New refusal codes: `vendordeposit.refund_too_much`, `.refunded`, `.refund_voided`, `.refund_account`, `mileage.paid_back`, `check.from_bank_line`, `channel.tax_move_accounts`, `donation.returned`, `.split_goods`, `.after_deposit`, `.return_before_deposit`, `allocation.pool_taken`, `gift_history.goods_description`, `gift.released` (grants), `loan.has_draws`, `prebill.work_changed`, `prebill.line_gone`, `creditmemo.time_billed_again`, `partner.account_taken`, `partner.account_retained`.
- **2026-11-24**: refunds of online payments and saved cards. Under `/api/sales`: `GET payments/{id}/online-refunds`, `POST payments/{id}/online-refund` (202 while the provider hasn't answered), `POST online-refunds/{id}/retry|abandon`, `GET|DELETE customers/{id}/saved-card`, `GET invoices/{id}/card-charges`, `POST invoices/{id}/charge-card`, `POST invoices/{id}/auto-charge`. `online-notices`: `paymentLinkId` may be null and `cardChargeId` is added. New email kind `CardAgreement`. Additive, apart from the nullable id.
- **2026-11-24**: sales reps. Invoices, sales receipts and credit memos take and return `salesRepId` (create: null takes the customer's responsible person, an empty id nobody; change: null keeps). `GET|POST /api/sales/commissions/runs` (accountant; 403 without purchases). Report `sales-commissions`. Additive.
- **2026-11-24**: use tax and backup withholding. Bill and vendor credit commands take `useTax`; bank transactions `useTax` and `holdBack`; `GET /api/sales-tax/use-tax/{documentId}`; liability rows have `isUseTax`. `GET /api/purchases/backup-withholding?year=`, `PUT /api/purchases/backup-withholding/vendors/{id}` (accountant); bill payments return `withheld` and `netPaid`, 1099 rows `withheld`, vendors `backupWithholdingFrom`. ACH payments' `amount` is now what's paid, after anything withheld (the same unless a vendor is marked). Reports `backup-withholding` and the use tax rows of the liability report. Additive otherwise.
- **2026-11-24**: `POST /api/erase/customers/{id}` and `/vendors/{id}` (`{ confirm }`, admin, closed to limited keys). New codes `erase.not_ready`, `erase.confirm`, `erase.already`, `customer.erased`, `vendor.erased`, `record.erased`. Additive.
- **2026-11-24**: `GET|PUT /api/settings/email` gain optional `signIn` (`Password` or `Microsoft`; signing in at Microsoft is done on the settings page). `GET|PUT /api/settings/bills-mailbox` (`password` write-only, `clearPassword`; `GET` reports `hasPassword` and the last check) and `POST /api/settings/bills-mailbox/check`; admins, closed to limited keys. New refusal codes `email.microsoft_*` and `mailbox.*`. Additive.
- **2026-11-24**: `PUT /api/settings/online-payments` closes the old provider's open payment links when the provider changes or its key is removed, and refuses with `online.links_open` when one can't be closed; optional `leaveLinks: true` changes anyway. Payments completed on a link OpenBook had closed are now recorded (Square, PayPal). Items return `isRetainage`; that item's type and account can't change (`item.retainage_locked`) and it can't be merged (`merge.item_retainage`). A behaviour change for a provider change that used to leave links open.
- **2026-11-21**: text messages through Twilio. `POST /api/sales/{kind}/{id}/text` and `/text/preview` (`{ body }`; kinds `invoices`, `estimates`, `credit-memos`, `receipts`), `GET /api/sales/{kind}/{id}/texts`, `PUT /api/customers/{id}/text-settings` (`{ number, allowed }`), `GET|PUT /api/settings/texts` (`accountSid`, `authToken` write-only, `sender`, `clearAuthToken`; `GET` reports `hasAuthToken` and `isReady`; admins, closed to limited keys). New audit action `Texted` (a new webhook event for documents texted). New refusal codes `text.*` and `customer.text_number_required`. Additive.
- **2026-11-19**: online payments through PayPal and Square as well as Stripe. `PaymentProvider` gains `PayPal` and `Square`. `GET/PUT /api/settings/online-payments` gain optional `provider`, `clientId`, `webhookId` and `sandbox` (a PUT without `provider` keeps the provider and PayPal's ids). New anonymous paths `POST /hooks/paypal`, `POST /hooks/square` and `GET /pay/{linkId}`. For PayPal links, the API's `url` is PayPal's own page, which lasts about three hours; send customers `/pay/{id}` instead. Additive.
- **2026-11-19**: Plaid bank feeds. `GET|PUT /api/settings/plaid` (`clientId`, `secret` write-only, `environment` `Sandbox` or `Production`, `clearSecret`; `GET` reports `hasSecret`; admins, closed to limited keys), `POST /api/banking/connections/plaid/link-token` (returns `{ linkToken }`, never replayed by `Idempotency-Key`), `POST /api/banking/connections/plaid` (`{ publicToken, name }`, returns the connection). Connections may have `provider` `Plaid`. `POST /api/banking/connections/{id}/disconnect` still answers 204, or 200 with `{ warning }` when the service couldn't be told (the connection is disconnected either way). New refusal codes `plaid.*`. Additive.
- **2026-11-15**: `GET|POST /api/nonprofit/households`, `GET|PUT|DELETE /api/nonprofit/households/{id}`, `GET /api/nonprofit/statements/{year}/households/{id}`, `GET|POST /api/nonprofit/gifts/{id}/soft-credits`, `DELETE /api/nonprofit/gifts/{id}/soft-credits/{creditId}`; reports `giving-by-household` and `soft-credits`; `giving-by-donor` gains a sixth column, Soft credits. `POST /api/nonprofit/statements/{year}/email` now sends a household's members one statement. Additive, apart from that grouping.
- **2026-11-15**: items take optional `unit`, `packUnit` and `packSize`, and return them with `variantOfId` and `variantOptions`; `POST /api/inventory/items/{id}/variants` (a list of `{ name, values }`) makes variants and returns them. Report `sales-by-style`. Additive.
- **2026-11-15**: invoice, sales receipt and credit memo create and change requests take optional `taxAmount` (the total tax, split between the code's rates); change requests also `workOutTax`. New refusals for a bad typed amount: `tax.typed_cents`, `tax.typed_out_of_range`, `tax.typed_no_rate`, `tax.typed_without_code`. Additive.
- **2026-11-15**: customers take optional `exemptionRegions` ("TX, OK"; blank covers every state), returned with `exemptionStartsOn` on customers and on `GET /api/sales-tax/exempt-customers`. Additive.
- **2026-11-14**: items take optional `weight` and `volume`; landed costs take `method` `ByWeight` or `ByVolume`. Additive.
- **2026-11-14**: `POST /api/nonprofit/pledges` and `/grants` take optional `alreadyOnBooks`; a pledge made so has `journalEntryId` of all zeros. Additive.
- **2026-11-14**: channel requests take optional `marketplaceWarehouseId`; order lines carry `byMarketplace`. Additive.
- **2026-11-14**: `POST /api/inventory/landed-costs` takes optional `itemIds` (only those items' stock gets the cost); `GET /api/inventory/landed-costs/candidates` adds `itemIds`. Additive.
- **2026-11-14**: `PUT /api/nonprofit/grants/{id}/budget` lines take optional `category`; a line with a category may have amount 0. The grant's budget rows carry `category`. Additive.
- **2026-11-14**: `POST /api/sales/estimates/{id}/progress-invoice` takes optional `retainagePercent`. Additive.
- **2026-11-14**: `POST /api/import/open-documents/{kind}` refuses (400 `opendocs.balance_unexplained`) when receivables or payables hold money no open document explains, unless `acceptUnexplained=true`; the plan has `unexplained`. A behaviour change for a case that used to import with a warning.
- **2026-11-14**: `GET /api/accountant/forecast` takes optional `lastYear=true`; each week then has `lastYear`, included in `closing`. Additive.
- **2026-11-14**: `POST /api/banking/feed/{id}/add` takes optional `billToCustomerId` and `markupPercent`, marking the expense billable to that customer. Additive.
- **2026-11-13**: added `POST /api/items/batch` (`dryRun`): one change (type, category, income account, taxable, active, price) to many items, returning each item's outcome. Additive; nothing existing changed.
- **2026-11-12**: policy written. Add-in API version 1 (`PluginApi.Version` = 1, `PluginApi.Oldest` = 1); add-ins that don't state an `ApiVersion` count as version 1.
