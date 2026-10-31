# Your chart of accounts

The *chart of accounts* is the list of buckets your money is sorted into: your bank accounts, what customers owe you, what you owe, your kinds of income and your kinds of expenses. Every transaction in OpenBook moves amounts between these accounts, and every report is built from them.

Open it from **Accounts** in the menu. Everyone can look at it; changing it, entering opening balances and importing need the accountant role.

## Start with a starter chart

A new company has no accounts. The Accounts page offers four starter charts, with the one matching your business type marked **Recommended**:

| Starter chart | For |
| --- | --- |
| Sole proprietor or single-member LLC | Owner's equity with contributions and draws; service and product income; common expenses |
| Partnership or multi-member LLC | A capital and draws account per partner; service and product income; common expenses |
| S or C corporation | Common stock, paid-in capital and distributions; payroll expenses and officer pay |
| Nonprofit | Net assets with and without donor restrictions; contributions, grants and program revenue |

Select **Use this chart**. You can rename, add and deactivate accounts afterwards; the starter chart is only a beginning.

You can also add accounts one by one (**New account**) or import your own list (**Import CSV**, below).

## Account types

Each account has a *type*. The type decides where the account appears on reports and which screens use it, so pick the narrowest one that fits.

| Type | Use it for | Shows on |
| --- | --- | --- |
| Bank | Checking, savings, petty cash, a payment processor balance | Balance sheet |
| Accounts receivable (A/R) | What customers owe you (OpenBook creates one when needed) | Balance sheet |
| Other current asset | Undeposited funds, inventory, prepaid expenses, short-term deposits | Balance sheet |
| Fixed asset | Equipment, vehicles, buildings, and their accumulated depreciation | Balance sheet |
| Other asset | Long-term deposits and other assets | Balance sheet |
| Accounts payable (A/P) | What you owe vendors on bills (OpenBook creates one when needed) | Balance sheet |
| Credit card | Each business credit card | Balance sheet |
| Other current liability | Sales tax payable, payroll liabilities, customer deposits, short-term loans | Balance sheet |
| Long-term liability | Loans and mortgages repaid over more than a year | Balance sheet |
| Equity | Owner's capital and draws, retained earnings, opening balance equity | Balance sheet |
| Income | What you earn from your main business | Profit and loss |
| Cost of goods sold | The direct cost of what you sell | Profit and loss |
| Expense | Running costs: rent, utilities, supplies, software | Profit and loss |
| Other income | Interest earned, gains on selling assets | Profit and loss |
| Other expense | Interest paid, losses | Profit and loss |

Bank and credit card accounts are what the **Banking** page lists. Accounts receivable and payable are used by invoices and bills automatically; you don't post to them by hand.

## Adding and changing accounts

**New account** asks for:

- **Name**, and an optional **Number**. If any account has a number, the list shows a number column and is sorted by it.
- **Type** (above).
- **Subaccount of**: put an account under another to group it, for example "Utilities:Electric" and "Utilities:Water". A subaccount has the same type as its parent, and accounts nest up to five levels. Reports show subaccounts indented under their parent with a total.
- **Description** (optional).
- **Tax line** (profit and loss accounts): where the account's total goes on your tax form (Schedule C, 1065, 1120-S, 1120 or 990, depending on your business type). The **Tax summary** report adds these up for your tax preparer. A subaccount without a tax line uses its parent's.

You can rename an account or change its number, description, parent or tax line at any time. Its type can change too, except that once something is posted to it, it has to stay in the same group: asset, liability, equity, income or expense.

### Accounts you no longer use

Accounts are never deleted. Open the account and make it **inactive**: it drops out of the pickers on new transactions but stays on old transactions and reports.

## Importing a chart from a CSV file

**Accounts → Import CSV** takes a spreadsheet saved as CSV with these columns (the names don't have to match exactly):

```csv
Number,Account,Type,Description
1000,Checking,Bank,Main operating account
1100,Accounts Receivable,Accounts Receivable,Money customers owe you
6100,Utilities:Electric,Expense,
```

Write subaccounts as `Parent:Child`. Optional columns are *Tax line* and, for nonprofits, *Donor restricted*. A sample file is on the import page.

OpenBook shows a preview first. If any row has a problem, nothing is imported until it's fixed, so you never end up with half a chart. Accounts whose names already exist are left as they are.

## Opening balances

If you're moving from other books, enter the balances you're bringing over: **Settings → Opening balances** (`/journal/opening-balances`).

1. Set **As of** to the day before you start using OpenBook, usually the last day of a month or year.
2. Enter each balance sheet account's balance from your last balance sheet: bank balances as on the statement, card balances as owed, loans as still owed.
3. Select **Post opening balances**.

Whatever doesn't balance goes to an equity account named "Opening Balance Equity". Your accountant can move it to the right equity accounts later.

**Leave accounts receivable and payable out.** Enter each unpaid customer invoice and vendor bill individually instead, dated when it was issued. That way you can receive payments and pay bills against them as usual.

Opening balances for many accounts can also be imported from a CSV file on **Settings → Import transactions**.

## Account registers

Every account has a **Register** (link on the chart of accounts): every transaction that touched it, with a running balance. Amounts show in the account's natural direction, so a bank register shows money in and out, and a card register shows charges and payments.

Accountants can enter a simple two-line transaction straight into a register using the quick-entry row at the bottom (`Alt` + `Q` jumps to it). Select any line to open the transaction behind it.

## Classes, locations, fixed assets and loans

The Accounts page also links to:

- **Classes and locations**: optional extra ways to slice your reports, such as by department, store or program. See [chapter 17](17-classes-locations-tags.md).
- **Fixed assets** and **Loans**: depreciation schedules and loan payments that post themselves. See [chapter 19](19-assets-and-loans.md).
