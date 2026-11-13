# API, webhook and add-in stability

For people building on OpenBook: scripts and integrations using the API, webhook receivers, the `openbook` command-line tool, and add-ins. It says what you can rely on from one release to the next, what may change, and how changes are announced.

OpenBook is before 1.0 (the latest release is 0.13.0). The policy below already applies; what changes at 1.0 is only where a breaking change may land (see "Rules for changes").

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

- **2026-11-13**: added `POST /api/items/batch` (`dryRun`): one change (type, category, income account, taxable, active, price) to many items, returning each item's outcome. Additive; nothing existing changed.
- **2026-11-12**: policy written. Add-in API version 1 (`PluginApi.Version` = 1, `PluginApi.Oldest` = 1); add-ins that don't state an `ApiVersion` count as version 1.
