# OpenBook interface conventions

How the general guidance in `SKILL.md` is already answered in this repository. Use these before inventing anything. `CLAUDE.md` at the repository root and the area notes it points to under `docs/dev/` (`docs/dev/ui.md` for pages) are the authority; this file gathers its interface rules in one place and adds what the stylesheet and components show. Paths are relative to `src/OpenBook.Web`.

## Contents

1. Who the users are
2. Page skeletons
3. Components to reuse
4. CSS classes and tokens
5. Script behaviours (data attributes)
6. Forms
7. Access, views and editions
8. Wording and formatting
9. Preferences already supported
10. Checks before calling a page done
11. Scorecard notes for this stylesheet

## 1. Who the users are

Bookkeepers and small-business owners, not accountants or developers. Many come from QuickBooks Desktop. Two audiences share every screen:

- **Simple view** (suggested at setup): people who want tasks, not ledgers. Plain question labels, accounting choices folded away.
- **Full view**: daily users who want density, keyboard speed and every option visible.

Use bookkeeping words people see on paper ("bill", "deposit", "money owed to you"), never ledger or data-model terms, on everyday forms and their error messages. Organisation and generic task names may feel familiar to QuickBooks users ("Create Invoices", "Pay Bills"); never copy Intuit's icons, artwork, colours or wording.

## 2. Page skeletons

Pages are static server-rendered by default; `@rendermode InteractiveServer` only where the form truly needs it (line grids, imports). Wrap content in `<div class="wide">` for the wide column (1080px; default 760px).

**List** (model: `Components/Pages/Vendors/Vendors.razor`)

```razor
<PageTitle>Vendors · OpenBook</PageTitle>
<div class="wide">
    <div class="toolbar">
        <h1>Vendors</h1>
        <ActionLink Class="button secondary" Href="/vendors/import">Import CSV</ActionLink>
        <ActionLink Class="button" Href="/vendors/new">New vendor</ActionLink>
    </div>
    @* empty: <div class="card"><p>No vendors yet. <ActionLink …>Add one</ActionLink>…</p></div> *@
    <div class="summary-bar">…label / value pairs…</div>
    <div class="table-wrap"><table class="data">…</table></div>
    <ShowMore Shown=… Total=… Href=… />
</div>
```

- One filled `button` (the primary action, last in the toolbar); everything else `button secondary`.
- First column is the record's name as a link. Amount columns `class="num"`. Inactive or voided rows `tr.inactive` plus a `<span class="badge">`.
- Document lists show the newest `RecentDocuments.PageSize` (100) with `<ShowMore>` (`?count=`). Never load a whole history for a page that runs often.
- The empty state is a card with a sentence and the action that fills it.

**Area hub** (model: `Components/Pages/Purchases/Purchases.razor`): title with one or two primary buttons, then `.hub-actions` with `.hub-group` sets labelled Create, Lists, Tools, Reports, Set up; then a `.summary-bar`; then tabs as `<nav class="toolbar" aria-label="Show">` of buttons where the current one is filled.

**Detail**: toolbar with the record's name and its actions (History, then related "New …" links with `?customer=` / `?vendor=` presets), `alert` lines for status, `summary-bar`, `dl.facts` for key facts, related tables (`.split-grid` for two side by side), then `<Attachments>`, `<Notes>`, `<CustomFieldPanel>`, `<EmailHistory>`, `<EntryTags>` as they apply.

**Report**: a `ReportDefinition` gets the generic page, exports and saving for free. Reports are static GET forms so every one has a shareable, printable address. Don't hand-build a report page unless it needs its own markup; then add `<ReportActions Key=…/>`.

**Settings**: add an entry to the `/settings` index with a sentence, a status line and filter keywords.

**Record not found**: a card with a sentence and a link back. Use `else`, never `return;` inside open markup.

## 3. Components to reuse

| Need | Use |
|---|---|
| Link that may need more access | `<ActionLink Href Class>` |
| Post that may need more access | `<PostForm Action>` (static) with `<AntiforgeryToken />` |
| A form the page handles itself (`EditForm`, `@formname`) that may need more access | `<FormAccess Action>` around what is inside the form |
| Choose a customer, vendor, item, account (interactive) | `SearchSelect` with `PickerOptions.…`; `CustomerAdded` / `VendorAdded` / `ItemAdded` gives "+ Add" |
| Same on a static form | `data-search` on the `<select>` / `InputSelect` |
| Advanced or accounting fields | `<MoreOptions Open=… HasContent=… Title=…>` |
| Long list paging | `<ShowMore>` |
| Link to the guide | `<HelpLink Chapter=… Section=…>` |
| Files, notes, custom fields on a record | `<Attachments>`, `<Notes>`, `<CustomFieldPanel>`, `<LineFields>` |
| Line grids | `SalesLinesEditor`, `BillLinesEditor`, `JournalLinesEditor` |
| Class, location, warehouse, tags | `ClassSelect`, `LocationField`, `WarehouseField`, `TagChecks` |
| Print, email | `<PrintButtons>`, `EmailCompose`, `<EmailHistory>` |
| Closed period password | `ClosedPeriodPassword` (interactive); static forms need nothing |
| Unsaved typing on new documents | `FormDraft<T>` |
| Correction history | `<DocumentChangeList>` |
| Something that only works when others can reach the server | `<DesktopNote>` |
| A ledger line's or audit record's page | `Links.For…` (`Components/Links.cs`) |

A new record type needs its label in `CenterTransactions`, a link in `Links.ForAuditEntity`, and a heading in `Search.razor` if it is searchable.

## 4. CSS classes and tokens

All in `wwwroot/app.css`. Colours are tokens: `--bg`, `--surface`, `--text`, `--muted`, `--border`, `--accent`, `--accent-text`, `--danger`, `--danger-bg`, `--notice-bg`, chart `--series-1/2`. Use tokens, never literal colours, so both themes work. Dark values are defined twice (the `prefers-color-scheme` block and `:root[data-theme="dark"]`); change both together.

- Layout: `.wide`, `.card`, `.narrow`, `.toolbar`, `.hub-actions` / `.hub-group` / `.hub-label`, `.summary-bar` (`.label`, `.value`, `.value.off`, `.value.zero`), `.split-grid`, `.row` (two columns, one under 560px), `.row.wrap`, `.tiles` / `.tile`.
- Filter box over a list: `input.filter` with `data-filter`.
- Forms: `.field` (label above input, optional `.hint`, `.hint.warn`), `.check`, `.check-list`, `.actions`, `.inline`, `.inline-actions`, `.segmented`, `details.more-options`.
- Buttons: `button` / `.button` (primary), `.secondary`, `.danger`, `.link`.
- Tables: `table.data`, `.num`, `tr.inactive`, `td.indent-1…5`, `tfoot`, `.table-wrap`, `.pager`.
- Status: `.alert.error` with `role="alert"`, `.alert.notice` and `.alert.success` ("Saved", "Done") with `role="status"`, `.badge`, `.badge.warn`, `.badge.ok`, `.out-of-balance`, `.muted`.
- Hidden from sight, kept for screen readers: `.visually-hidden`.
- Drop-downs: `details.menu` (one open at a time; Esc and outside clicks close them).

Add a class only when none of these fits, and put it in `app.css` beside its relatives.

## 5. Script behaviours (data attributes)

Prefer these to new script. They live in `wwwroot/shortcuts.js`, `searchselect.js`, `forms.js`, `email.js`, and are re-applied after enhanced navigation.

| Attribute | Effect |
|---|---|
| `data-confirm="…"` | Ask before the button acts. Never an inline `onclick` |
| `data-search` | Searchable picker on a static select; `data-keywords` on options; `data-options="#id"` for one shared list in a grid |
| `data-filter="#list"` with `data-filter-item` / `-group` / `-empty` | Filter box over a list |
| `data-shown-by="#select" data-shown-for="A B"` | Show a form part only for some choices |
| `data-keep-typed` | Bring a form's values back after a refusal |
| `data-unsaved-watch`, `data-needs-saved` | Ask before acting on a form with unsaved changes |
| `data-print`, `data-print-form` | Open the print dialog without leaving the page |
| `data-check-all` (header box of a table) | Tick every row shown; `[data-checked-count]` in the same form counts the ticked rows, `[data-needs-checked]` buttons wait for one (`forms.js`) |

Keyboard: `/` search, `?` shortcut list, `g` + key go, `n` + key new, `Ctrl+Enter` save, `Alt+Q` quick entry, `g` then `?` help for this page. A new area gets its prefix in `HelpTopics.For` and, where it earns one, a `g` or `n` key.

## 6. Forms

- Static forms are plain HTML posts with `<AntiforgeryToken />`; grids and multi-file posts bind `IFormCollection`. Numbers go through `FormNumbers`: blank is null, unreadable text is refused, never read as blank.
- Labels above inputs in `.field`; a `.hint` under the input says what the field is for or where it is used.
- The save button goes in `.actions` (that is what guards a double click). "Add line" buttons don't.
- Interactive handlers start `if (_busy) return;`, call services only through `Operations.RunAsync`, and keep what was typed when a save is refused.
- Refusals show as `<div class="alert error" role="alert">` above the form. Static posts redirect back with the message; interactive forms stay put.
- Nothing is refused where a warning will do: possible duplicates and credit limits turn the button into "Save anyway".
- Documents are corrected in place on a Change page with a reason, or voided. There is no delete, so "undo" means void, deactivate, or take back; say which.
- Edit pages of versioned records note the version when they open and refuse a stale save (`save.stale`).
- New-document pages take `?copy={id}`, `?customer=`, `?vendor=`, `?item=` presets; keep supporting them.
- Amount fields are pre-filled with `ToString("0.00")` and parsed with "." as the decimal point; only regions that write "1,234.56" are offered.

## 7. Access, views and editions

- **Pages never hide what someone can't do.** `ActionLink` and `PostForm` grey it out, say what it needs, and lead to the page naming who can grant it. Menus are the exception: they hide unreachable links with `CanReach(href)`. Use `CanPost(action)` for forms; not `User.Can(role)` in components.
- **View** (Simple / Full), **edition** (General, Professional Services, E-commerce, Nonprofit) and **install** (Desktop / Server) change only what is shown first and how it is worded. Never make data, rules or access depend on them, and never remove a page from someone who could otherwise reach it. Use the three words exactly; they are different things.
- `Editions.Terms` gives the word for customers (Donors / Clients / Customers).
- A new page that changes the books needs a rule in `AccessRules` and an area in `PageAreas`.

## 8. Wording and formatting

- Keep the product's own words (Replenish, Float, Custodian, Undeposited Funds, Void). They are in the user guide, the messages from the services and people's habits; a clearer page explains them, it doesn't rename them.
- The string catalog (`SharedResource.resx`) is produced by `python tools/extract-strings.py`. If you can't run it, say the catalog still needs regenerating; don't edit it by hand and call it regenerated.
- Sentence case. Buttons name the action and the thing ("New vendor", "Save anyway", "Mark them all").
- Page title `<PageTitle>Thing · OpenBook</PageTitle>`.
- Text through `@L["English text"]`, arguments in the call, whole sentences as one key; never join fragments. Two texts must not differ only in case. A new page is written with `@L` from the start. On an existing page, write the lines you add with `@L`; convert the rest of the page only when the work reworks that page, not when you add a link to it. Run `python tools/extract-strings.py` after either.
- Format with `Labels` (`Components/Labels.cs`): `Amount`, `AmountOrBlank` (blank for zero in tables), `Date`, `Quantity`, `Price`, `Percent`, `Terms`, `Status`, `For(enum)`.
- "Today" is `CompanyTime.Today`; timestamps on pages go through `InCompanyTime()`.
- When a label, menu path or rule changes, update the matching chapter in `docs/user-guide/` in the same commit; it is also the in-app help.

## 9. Preferences already supported

Don't add a second mechanism for any of these.

| Preference | Where it lives |
|---|---|
| Theme Auto / Light / Dark | `localStorage` `ob-theme`, `data-theme` on `<html>` |
| Language and region | `.AspNetCore.Culture` cookie, else the browser's |
| Simple or Full view, menu style | `ViewPreference` per user; company default |
| Hidden dashboard panels | `DashboardPreference` per user |
| Preview fit and size | `localStorage` |
| Unsaved drafts | `sessionStorage` through `FormDraft<T>` |
| Document dates, print and email format | the company's region, not the reader's |

Per-person presentation choices are stored unaudited beside the books; anything that changes the company's records is audited.

## 10. Checks before calling a page done

1. Open it in the browser (`.claude/launch.json`, port 5188), light and dark.
2. Narrow to 375px: no sideways page scroll; tables scroll inside `.table-wrap`; toolbars wrap.
3. Do the main task with the keyboard only.
4. Open it as a view-only user: actions greyed with the reason, nothing missing.
5. Look at it in both the Simple and Full views.
6. Empty company, one record, and a long name.
7. A page counts as fixed only when it passes on SQLite and PostgreSQL; only PostgreSQL really waits, so loading states differ.
8. `PageSmokeTests` must still pass; a new prefix needs its place in `AccessRulesTests`.

## 11. Scorecard notes for this stylesheet

Settled on 2026-11-10; keep them so.

- Body text is `1rem`, so it follows the size people set in their browser (16px by default). Size text in `rem` or `em`, never `px`.
- Links, buttons, `summary` and anything with a `tabindex` get the accent focus ring on keyboard focus (`:focus-visible`); inputs get it on any focus. Never remove an outline without putting something as visible in its place.
- `prefers-reduced-motion` switches off animation and transitions everywhere. The stylesheet itself has none; if you add motion, keep it short and non-essential.
- Phone rules start at 760px. The other breakpoints (560, 800, 860, 900, 1000px) are where one particular layout stops fitting (form rows, the centers, side-by-side tables, the editors with a preview, the workflow home); pick a breakpoint by when the content breaks, and use 760px for anything meant for phones.

Not measured here: contrast of every token pair, target sizes of small controls (`.badge`, `button.link`, table links), and anything needing real users.
