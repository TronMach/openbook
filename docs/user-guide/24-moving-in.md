# Moving in from QuickBooks and other programs

There are two ways to bring your books into OpenBook. Which suits you depends on how much history you want.

| Approach | You bring | Good for |
| --- | --- | --- |
| **Start fresh** from a date | Opening balances, open invoices and bills, your lists | Most small businesses. Clean, quick, and your old program keeps the history |
| **Import the history** | Accounts, customers, vendors, items and every past transaction | When you want past years' reports inside OpenBook |

Either way, pick a start date at a period end (a month, quarter or ideally a year end) and keep your old program, or exports from it, for the years before.

All importing needs the accountant role.

## Starting fresh

1. **Lists**: import your chart of accounts ([chapter 3](03-chart-of-accounts.md#importing-a-chart-from-a-csv-file)), customers, vendors and products from CSV files exported from your old program. Each list page has an **Import CSV** button.
2. **Opening balances** as of the day before your start date, from your old program's balance sheet ([chapter 3](03-chart-of-accounts.md#opening-balances)).
3. **Open invoices and bills**: enter each unpaid customer invoice and vendor bill, dated when it was issued, so you can receive payments and pay bills against them. Leave accounts receivable and payable out of the opening balances.
4. **Bank reconciliation**: start each bank account's first reconciliation from the statement that covers your start date, and tick the uncleared checks and deposits you entered.

## Importing the history

**Settings → Move in from another program** (`/import/migrate`), or while setting up the company: **Read the file** on the setup page fills in the company's details from the export and imports it when the company is created ([Getting started](01-getting-started.md#set-up-your-company)). Choose where your books are now under **From**, upload the export file, and look at the preview.

| From | What to export | What's read |
| --- | --- | --- |
| QuickBooks Desktop (IIF) | File › Utilities › Export › Lists to IIF Files, plus transactions as exported by QuickBooks or another tool | Accounts, customers, vendors, products and services, and transactions; estimates and orders are skipped |
| QuickBooks Desktop (qbXML) | The files OpenBook's QuickBooks export helper saves (see [below](#quickbooks-desktop-company-files-qbw-qbm-qbb)), or other SDK responses saved from a query tool | From the helper: the company, accounts, customers, vendors, items and every transaction. Other qbXML files: journal entries only, other transaction types are listed but not imported. Jobs are listed but not imported |
| QuickBooks Online | API query responses saved as JSON (for example from Intuit's API Explorer) for Account, Customer, Vendor, Item and JournalEntry | The same; include the accounts, because journal lines find them by id |
| GnuCash | File › Save As, XML format (compressed is fine) | The account tree and every transaction in the book's currency |
| Xero | Chart of accounts, contacts or manual journals as CSV, one file at a time | Whichever it is, recognized by its columns; journal lines find accounts by code |
| Wave | Settings › Data export › Export accounting transactions (CSV) | Accounts, customers, vendors and every transaction |
| FreshBooks | Clients › Export (CSV) | Clients, as customers; bring balances in with opening balances or a journal entry CSV |

Files can be up to 20 MB; export larger books in parts (lists, then transactions by year).

### QuickBooks Desktop company files (.QBW, .QBM, .QBB)

OpenBook can't open a QuickBooks company file itself: the format is Intuit's own, and only QuickBooks reads it. The **QuickBooks export helper** asks QuickBooks for everything instead and saves it as files OpenBook imports. It brings the whole history: every transaction QuickBooks has posted, of any type, comes from QuickBooks' journal.

1. **Get the helper.** OpenBook Desktop installs it: Start menu › **OpenBook - Export from QuickBooks**. On a server, **Settings → Move in from another program** with **From** set to **QuickBooks Desktop (qbXML)** has a **Download the QuickBooks export helper** link when the server has it; otherwise ask whoever runs OpenBook.
2. **Open the company in QuickBooks Desktop** on that Windows PC. A portable file (.QBM) or backup (.QBB) has to be restored first: in QuickBooks, **File › Open or Restore Company**, then **Restore a portable file** or **Restore a backup copy**.
3. **Run the helper.** The first time, QuickBooks asks whether to let "OpenBook export" read the company. Sign in to QuickBooks as the admin in single-user mode and choose **Yes, this time** (or **Yes, whenever this QuickBooks company file is open**). The helper shows what it reads (accounts, customers, vendors, items, then the journal year by year) and saves the files in **Documents › OpenBook QuickBooks export**, named after the company.
4. **Import the files** under **Settings → Move in from another program**, **From: QuickBooks Desktop (qbXML)**. A large company is saved in several files ("part 1 of 3" and so on): import them one at a time and in order. The first one also works for **Read the file** when setting up a new company.

Good to know:

- Active customers, vendors and items come over; every account comes over, including inactive ones, because old transactions use them. Jobs (sub-customers) aren't imported; their transactions are, as journal entries.
- Each transaction becomes one journal entry with QuickBooks' type, number and name in its memo (for example "Invoice 1001 · Acme"). As with every import, invoices and bills don't become documents, so the A/R and A/P aging won't list them (see [What to expect](#what-to-expect)).
- The helper goes back month by month until it finds five years in a row with nothing. If your books have an older gap than that, run it from a command prompt with `--from` and the first date, for example `OpenBook.QuickBooksExport --from 2005-01-01`. `--help` lists the other options (another folder, a date range, a company file for QuickBooks to open).
- If your antivirus stops the helper, it's because the program is new and not yet signed; allow it, or ask whoever runs OpenBook.

### What to expect

- **Nothing is imported unless every row is valid.** The preview shows what will come in and lists any problems. Fix them in the file and upload again.
- **What's already in is left as it is**, so you can import lists first and transactions later, or run an import twice without duplicates. Names you already have aren't touched. A transaction with the same date, memo, accounts and amounts as one from an earlier import is shown as already there and left out, and the preview says how many; if one is a genuine repeat, enter it by hand. After **Undo the whole import**, the file can come in again.
- **Transactions come in as journal entries**, one each, so balances and accrual reports are right. On the cash basis an imported invoice or bill counts when it was entered, not when it was paid, so take the move-in year's cash-basis figures, and its 1099 totals, from your old program. They don't become invoices or bills, so the A/R and A/P aging won't list them; enter still-open invoices and bills as documents (and leave them out of the imported history, or reverse their effect with a journal entry, so they aren't counted twice).
- OpenBook's subaccounts must have their parent's type. A GnuCash account whose type differs from its parent's becomes a top-level account of its own.
- One currency only: transactions in other currencies are refused. Lines on GnuCash trading accounts are left out when they cancel each other, as they do on a home-currency stock purchase; otherwise the transaction is refused.

Imported entries are marked as an import batch. To undo an import, open any of its entries from the **Journal** and use **Undo the whole import**: with a reason, **Void all … entries** voids every entry from that file that's still standing, all at once. If one of them can't be voided (it's been reconciled, or it's in a closed period and you don't have the closing password), none are. Accounts, customers, vendors and products the import added stay; make them inactive if you don't want them.

## Importing transactions from a spreadsheet

For anything else, **Settings → Import transactions** takes a CSV file of:

- **Journal entries**: date, account (by full name like `Utilities:Electric` or by number), and debit and credit columns or one signed amount. An entry number column groups lines into entries; without one, lines are grouped until they balance. Optional class, location and tags columns.
- **Opening balances**: account and balance (in the account's natural sign) or debit and credit, as of a date.

As with every import, there's a preview, and any error stops the whole file.
