# For accountants and bookkeepers

This chapter is for the outside accountant, CPA or bookkeeper whose client keeps their books in OpenBook. It covers how far you can rely on what you see, how to get in, how month-end and year-end work, which reports you'll use, and what OpenBook doesn't do. The rest of the guide is written for the business owner; this chapter links to it where the detail is.

## How the ledger works and why you can trust it

### One ledger, balanced entries

Every transaction, whatever screen it was entered on, ends up as a journal entry in one general ledger. An invoice, a bill, a bank deposit, a stock adjustment, a depreciation run: each posts its own entry. Nothing writes to the ledger any other way, and every entry's debits equal its credits.

The figures kept beside the ledger (the A/R and A/P agings, what's been paid on each invoice and bill, Undeposited Funds, stock value, loan and asset registers and so on) are subledgers. OpenBook checks every day that they still agree with the ledger (see [Checking the books](#checking-the-books) below).

### The database refuses what shouldn't happen

The rules aren't only in the program. The database itself (SQLite or PostgreSQL) holds rules that refuse:

- an entry whose debits and credits don't agree;
- adding a line to an entry once it's posted;
- changing a posted entry or its lines (other than recording that it was voided);
- deleting an entry, a line, or a record of the audit log.

So even someone who opens the company file with a database tool can't quietly edit or remove a posted entry without first taking those rules apart. A backup is refused on restore if it doesn't balance or is missing any of these rules.

### Corrections are reversals, never edits

Posted entries are never edited or deleted. There are two ways to correct one:

- **Void**: OpenBook posts the exact opposite entry *on the same date*, with a reason. Both stay in the books, marked voided, and their effect cancels out in every report, including reports by class and location.
- **Change**: on a journal entry someone typed in, **Change** voids it with your reason and posts the corrected entry with the next number, together. On invoices, customer and bill payments, sales receipts, credit memos, bills, vendor credits, expenses, checks, deposits and transfers, **Change** works on the document itself: OpenBook voids its old entry and posts the corrected one, keeping the document's number and the payments applied to it.

An entry posted by a document is corrected from that document; its journal entry page says which document it came from. A transaction that has been reconciled can't be voided until its reconciliation is undone (see [Reconciliations](#reconciliations)).

### Every change has an audit record

Each change is written to the audit log in the same save as the change itself: who made it, when, what kind of change it was, and the fields before and after. If the audit record can't be written, the change isn't saved either. The log can't be edited or deleted, even by an administrator. Most record pages have a **History** link that opens the log for just that record.

### The audit chain and its fingerprint

Shortly after each change is saved, OpenBook links its audit record to the one before by a SHA-256 hash worked out from both: the *audit chain*. Each link's hash depends on every record before it, so changing or deleting any record afterwards, even by editing the company file directly, breaks the chain from that point. The chain is protected by the same kind of database rules as the audit log.

The books check's last check, **The audit trail is unchanged**, works every link out again and reports where the chain stands: a *position* (how many records are chained) and a *fingerprint* (the start of the latest hash).

A chain held inside the company file has one weakness: someone with the file and the know-how could rewrite a record and then work every hash out again. The answer is a copy of the fingerprint kept outside OpenBook:

- the **Year-end package** (below) writes the position and the full fingerprint into its README;
- OpenBook writes the position and fingerprint to its log file once a day;
- each backup holds the chain as it was then.

Keep the package README (or note the position and fingerprint) each time you sign off a period. Later, whoever looks after the client's installation can run `verify-audit --fingerprint <position>:<fingerprint>` to confirm that position still has that fingerprint, or, with the single company file, `verify-audit --against <backup file>` to compare with a backup. If anything differs, the audit trail was rewritten after the fingerprint was noted, or the company was restored from an older backup since. The command only reads, and is described in the admin guides (`docs/admin/desktop.md`, `docs/admin/server.md`).

So the chain shows that history up to a fingerprint you kept hasn't been rewritten since. It doesn't show that every record is genuine: a made-up change entered later looks like any other, with its own audit record and the name of whoever was signed in. And someone with the database, the log files and every backup could rewrite all of them; your own copy of the fingerprint is the one they can't reach.

## Getting access

### Being invited

Ask an administrator at your client to invite you: **Settings → Users and invitations** → **Invite someone**, with your email and a role. They can set **Access ends on**, for example after tax season, and **Link works for** (7 days unless they choose otherwise). You follow the link, choose your own username and password, and you're signed in. From the day your access ends you can't sign in and your API keys stop working. The details are in [Inviting someone](08-users-and-backups.md#inviting-someone).

### Which role you need

| Role | What it means for you |
| --- | --- |
| View-only | Reads lists, documents and most reports, and exports them. Not enough for the audit log, **Accountant tools**, the trial balance worksheet or the adjusting journal entries report |
| Standard | Also enters and voids day-to-day work, reconciles, adds notes and files |
| Accountant | Also the books themselves: chart of accounts, journal entries and opening balances, adjusting entries, closing the books, reclassifying, merging duplicates, imports, budgets, tax lines, most settings, the audit log and **Accountant tools** |
| Admin | Also users and invitations, backups and restore, **Export all data**, email and sign-in settings |

Most outside accountants need the accountant role. Note that backups and the full data export need an administrator. Instead of a role, a client can give you a **permission set** with a level for each area of the books ([Permission sets](08-users-and-backups.md#permission-sets)).

### Two-factor sign-in

Turn on two-factor sign-in for yourself: select your username in the top bar, then **Two-factor sign-in**. A client can require it for accountants and admins (**Settings → Sign-in and two-factor** → **Require it for**); you're then asked to set it up at your first sign-in. If the client uses single sign-on, you may be invited to sign in with your Microsoft or Google account instead. See [Two-factor sign-in](08-users-and-backups.md#two-factor-sign-in).

### More than one client

Each OpenBook installation keeps one company. There's no firm account or client list: each client's OpenBook has its own address, and you sign in to each separately.

A client on **OpenBook Desktop** can only open OpenBook on that one computer, so you can't sign in from your office. Work from the **Year-end package** they send you, or at their computer; for you to sign in remotely they need **OpenBook Server** (see [Desktop or Server](08-users-and-backups.md#desktop-or-server)).

## Month-end and year-end

### Reconciliations

Bank and credit card accounts are reconciled from **Banking** → **Reconcile** ([Reconciling](06-banking.md#reconciling)). Each finished reconciliation has a **Reconciliation report**, which can carry the bank statement as an attachment. To correct a reconciled transaction, open the account's latest reconciliation report and select **Undo reconciliation**: it reopens with its ticks kept. The home page lists accounts not reconciled through the end of last month.

### Checking the books

**Accounts → Accountant tools → Check the books** compares the subledgers with the ledger. It only reads, and it runs by itself once a day; **Check again** runs it now. Each of its 17 checks says **Agrees**, **Agrees, with a note** or **Problem**, and a problem lists the records involved, each a link:

1. **Every entry balances**
2. **Documents and their entries agree**
3. **Invoices show what's been paid**
4. **Bills show what's been paid**
5. **A/R aging matches the books**
6. **A/P aging matches the books**
7. **Undeposited funds match what's waiting**
8. **Stock value matches the books**
9. **Customer deposits match the retainers**
10. **Received Not Billed matches the deliveries**
11. **Pledges receivable match the open pledges** (nonprofits)
12. **Grants receivable and advances match the grants** (nonprofits)
13. **Accumulated depreciation matches the asset register**
14. **Loan balances match the books**
15. **Reconciliations still add up**
16. **No stock below zero unless allowed**
17. **The audit trail is unchanged** (above)

A **note** means the figures agree once entries made straight to an account are counted, such as a journal entry to accounts receivable, which an aging can't list; those entries are listed. What each check compares is in [Checking the books](20-budgets-and-analysis.md#checking-the-books).

### Adjusting entries

Enter an adjusting entry from **Accountant tools** → **Enter an adjusting entry**, or tick **Adjusting entry** on any **New journal entry**. An entry already posted can be marked from its page with **Mark as an adjusting entry** (and unmarked with **Not an adjusting entry**). Only journal entries can be adjusting entries, not entries posted by documents or automatic reversals.

On the journal entry form:

- **Counts toward**: **Both bases**, **Accrual reports only** or **Cash reports only**, for an adjustment that belongs to one basis.
- **Reverse on**: posts the opposite entry on that date, for accruals you reverse at the start of next month. Voiding the entry voids its reversal too.

The **Trial balance worksheet** shows each account's balance before the adjustments, the adjustments of the fiscal year up to the date, and the balance after. **Adjusting journal entries** lists them with their lines.

### Closing the books

**Settings → Closing date** (also **Accounts → Close the books**), accountant role:

- **Close the books through**: nothing dated on or before it can be entered, voided or changed.
- **Closing date password** (optional): with one, someone who knows it can still change a closed period, by typing it when OpenBook asks. Each change made with it shows in the audit log as a **Closed-period change**. Without one, closed periods can't be changed at all until the date is moved. Keep the password to yourself if you want to be the only one who can change filed periods.
- **Fiscal years** lists each year with its accrual net income; **Close year** moves the closing date to the year's last day.
- The page's **History** button lists every change to the closing date.

**Reopening**: move the date earlier, or clear it to reopen every period. Moving it earlier, clearing it, or changing or removing the password needs the current closing date password.

No closing entries are posted. Income and expense accounts show the current fiscal year, and the balance sheet and trial balance carry every earlier year's net income into retained earnings (into a top-level equity account named "Retained Earnings" if the chart has one, as the starter charts do; otherwise a computed **Retained earnings** line). Don't post a closing entry of your own: dated in the year, it would bring that year's profit and loss to zero.

### Changes to a prior year

Reports are worked out from the ledger, and a void is dated the same day as what it reverses, so voiding or changing something in a past year changes that year's figures in every report, and this year's retained earnings with them. If that year is closed, the change needs the closing date password and is recorded as a **Closed-period change**.

If the client has filed for a year and you'd rather leave it as filed, close it without a password, or keep the password yourself, and date corrections in the open period instead. A partners' profit allocation doesn't follow a late change by itself: **Void** the year's allocation and **Allocate** again ([Partners and their capital](20-budgets-and-analysis.md#partners-and-their-capital)).

### The year-end package

**Accounts → Accountant tools → Year-end package**: choose **From**, **To** and **Basis** (the company's unless you choose) and **Download package**. It's a ZIP of Excel files:

- Trial balance, Trial balance worksheet, Adjusting journal entries
- Profit and loss, Balance sheet, Statement of cash flows
- General ledger, Journal
- A/R aging, A/P aging
- Fixed asset register, Depreciation schedule, Loans
- 1099 summary, Tax summary
- for a nonprofit: Statement of activities, Statement of financial position, Statement of functional expenses, Restricted net assets and Grants
- when the client has any: Mileage log, Sales tax liability, Inventory valuation, Sales by channel and Sales tax by state (online orders)

plus the 1099 filing data as a CSV (for the last calendar year that ended by the package's **To** date) and a README with the basis and the audit trail's position and fingerprint. Making a package is recorded in the audit log.

**1099 filing data** on the same page downloads that CSV on its own for any **Tax year**, for the IRS IRIS template or a filing service. It holds full taxpayer IDs, so keep it private.

## Reports you'll use

**Reports** lists every report in groups. The ones accountants use most:

| Group | Reports |
| --- | --- |
| Accountant | Account list, Trial balance, General ledger, Journal, Trial balance worksheet, Adjusting journal entries, and the Transaction list (every transaction, newest first) |
| Financial statements | Profit and loss, Balance sheet, Statement of cash flows, Profit and loss by class, Profit and loss by location, Key performance indicators |
| Taxes | Tax summary, 1099 summary, 1099 detail, Sales tax liability, Estimated tax worksheet, and **Tax lines** |
| Inventory | Inventory valuation, Inventory roll-forward (the cost of goods sold figures for a return) |
| Custom | Custom transaction report |

The A/R aging and A/P aging are under the sales and expenses groups; fixed assets, loans and nonprofit statements have groups of their own.

**Basis**: each report has its own **Basis**, accrual or cash; it opens on the company's choice (**Settings → Company details** → **Reports open on**). Switching basis never changes the books. On cash basis invoices and bills count when they're paid, and a payment covering several invoice lines is split across their income accounts in proportion. Entries marked **Accrual reports only** or **Cash reports only** count on that basis alone. See [Accrual or cash basis](07-reports.md#accrual-or-cash-basis).

**Comparisons**: the profit and loss compares with the previous period or the same period last year, or shows months or quarters; the balance sheet compares with a month or a year earlier. See [Comparing periods](07-reports.md#comparing-periods).

**Drilling down**: amounts in the statements open the general ledger lines behind them, and from there each transaction.

**Exports**: every report exports to **CSV**, **Excel** or **PDF**, with exactly what's on screen. **Save report** keeps its settings (a saved "last month" stays relative), and **Reports → Scheduled reports** emails a saved report as PDF, Excel or CSV on a schedule, for example the monthly profit and loss to the owners ([Scheduled reports](20-budgets-and-analysis.md#scheduled-reports)). A report's settings are in its address, so you can send its link to someone who can sign in.

**Tax lines and the tax summary**: **Reports → Tax lines** assigns income and expense accounts to the lines of Schedule C, Form 1065, 1120-S, 1120 or 990 (the starter charts come with them filled in). **Tax summary** totals the accounts by those lines; for a partnership or S corporation it includes Schedule K lines, and on Forms 1065, 1120-S and 1120 a **Books to return (a Schedule M-1 worksheet)** section. See [Taxes](07-reports.md#the-main-reports).

**The audit log**: **Settings → Audit log** (accountant role) lists every change: **When**, **User**, **Change**, **Record** and the fields before and after. Filter by **Type**, **User**, **From** and **To**. The page has no export of its own; an administrator's **Export all data** includes it with every other table, as CSV.

## Working with the client

- **Notes**: accounts, customers, vendors, products and services, documents, journal entries and reconciliations have **Notes**, each showing who wrote it and when. They're a good place for a question about a record or the reason for an adjustment. A note doesn't notify anyone, so tell your client where to look. See [Notes](25-custom-fields-notes-attachments.md#notes).
- **Attachments**: the same records take files (**Attach**, up to 10 MB): put a workpaper on your adjusting entry, or the bank statement on a reconciliation. Files are kept in the company file, so they're in every backup.
- **Reclassifying**: classes and locations are kept beside the ledger, so they can be corrected without posting anything: **Accounts → Classes and locations** → **Reclassify transactions**, filter, **Show lines**, tick and **Reclassify selected lines**. Each change is in the audit log, and lines in a closed period need the closing date password. Tags can be changed on a transaction's page at any time. See [Classes, locations and tags](17-classes-locations-tags.md#fixing-them-afterwards).
- **Wrong account on a document**: use **Change** on the document rather than a journal entry, so the document, the aging and the ledger stay in step.
- **Duplicate customers, vendors or items**: merge them from the duplicate's page (accountant role; it can't be undone). See [Merging duplicates](04-sales.md#merging-duplicates).
- **From the command line**: with an API key (**Settings → API keys**, anyone can make one for themselves), `openbook check` runs the books check from your own computer and exits with code 3 when something doesn't agree ([The command line](26-offline-cli-api.md)).

## Limits worth knowing

- **No payroll.** Record payroll from the client's payroll service as journal entries (a recurring entry, or **Settings → Import transactions** for a CSV of them).
- **One currency.** The home currency is set once and can't change. There are no foreign currency transactions or revaluations.
- **One company per installation.** No consolidation, intercompany entries or firm-wide client list.
- **US tax forms.** Tax lines, the tax summary, 1099s and the estimated tax worksheet follow US federal forms. OpenBook prepares figures; it doesn't file returns or e-file 1099s.
- **No K-1s.** **Partners and their capital** splits the year's net income by fixed shares and rolls each partner's capital forward. Guaranteed payments, special allocations and separately stated items are yours to work out.
- **History brought in from other programs** comes in as journal entries. The agings don't list those amounts, and on the cash basis an imported invoice or bill counts when it was entered, not when it was paid, so take the move-in year's cash-basis and 1099 figures from the old program ([Moving in](24-moving-in.md)).
- **No closing entries.** Retained earnings is worked out, not posted (see [Closing the books](#closing-the-books)).
- **Backups and the full data export need an administrator.** With the accountant role you can download the year-end package and export reports, but not the company file.
- **Nothing is deleted.** Customers, vendors, items, accounts and users are made inactive; posted entries are voided.
