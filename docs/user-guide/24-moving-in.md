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

**Settings → Move in from another program** (`/import/migrate`). Choose where your books are now under **From**, upload the export file, and look at the preview.

| From | What to export | What's read |
| --- | --- | --- |
| QuickBooks Desktop (IIF) | File › Utilities › Export › Lists to IIF Files, plus transactions as exported by QuickBooks or another tool | Accounts, customers, vendors, products and services, and transactions; estimates and orders are skipped |
| QuickBooks Desktop (qbXML) | SDK responses saved from a query tool | Accounts, customers, vendors, items and journal entries; jobs and other transaction types are listed but not imported |
| QuickBooks Online | API query responses saved as JSON (for example from Intuit's API Explorer) for Account, Customer, Vendor, Item and JournalEntry | The same; include the accounts, because journal lines find them by id |
| GnuCash | File › Save As, XML format (compressed is fine) | The account tree and every transaction in the book's currency |
| Xero | Chart of accounts, contacts or manual journals as CSV, one file at a time | Whichever it is, recognized by its columns; journal lines find accounts by code |
| Wave | Settings › Data export › Export accounting transactions (CSV) | Accounts, customers, vendors and every transaction |
| FreshBooks | Clients › Export (CSV) | Clients, as customers; bring balances in with opening balances or a journal entry CSV |

Files can be up to 20 MB; export larger books in parts (lists, then transactions by year).

### What to expect

- **Nothing is imported unless every row is valid.** The preview shows what will come in and lists any problems. Fix them in the file and upload again.
- **Names you already have are left as they are**, so you can import lists first and transactions later, or run an import twice without duplicates.
- **Transactions come in as journal entries**, one each, so balances and reports are right. They don't become invoices or bills, so the A/R and A/P aging won't list them; enter still-open invoices and bills as documents (and leave them out of the imported history, or reverse their effect with a journal entry, so they aren't counted twice).
- OpenBook's subaccounts must have their parent's type. A GnuCash account whose type differs from its parent's becomes a top-level account of its own.
- One currency only: transactions in other currencies are refused.

Imported entries are marked as an import batch. To undo an import, void its entries from the **Journal**.

## Importing transactions from a spreadsheet

For anything else, **Settings → Import transactions** takes a CSV file of:

- **Journal entries**: date, account (by full name like `Utilities:Electric` or by number), and debit and credit columns or one signed amount. An entry number column groups lines into entries; without one, lines are grouped until they balance. Optional class, location and tags columns.
- **Opening balances**: account and balance (in the account's natural sign) or debit and credit, as of a date.

As with every import, there's a preview, and any error stops the whole file.
