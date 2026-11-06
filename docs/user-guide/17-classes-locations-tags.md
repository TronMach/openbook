# Classes, locations and tags

Your chart of accounts says *what* money was spent or earned on. Classes, locations and tags add *where* or *for what*, without adding accounts: the profit of each store, the cost of each program, everything spent on the spring trade show.

| | Applies to | Use it for | Example |
| --- | --- | --- | --- |
| **Class** | Each line | Departments, product lines, programs, partners | One invoice for Consulting (class: Advisory) and Training (class: Education) |
| **Location** | Each transaction | Stores, offices, regions, divisions | Everything on a bill belongs to the Denver store |
| **Tag** | Each transaction, several at once | Events, campaigns, anything ad hoc | "Trade show 2026", "Grant application" |

Classes and locations can nest (Retail:East:Boston), up to five levels. Tags are a flat list.

## Setting up

**Accounts → Classes and locations** (`/segments`):

1. Under **On transaction forms**, tick **Track classes on each line** and/or **Track a location on each transaction**. This only shows or hides the fields on forms; turning it off later keeps everything already recorded. (Accountant role.)
2. **Call locations** something that suits you: Department, Division, Store, Property. The forms and reports then use that word.
3. Add classes, locations and tags. Use **Under** to nest one inside another.

Make one inactive when you stop using it; it stays on old transactions and reports.

## Recording them

With tracking on:

- **Invoices, sales receipts, estimates, credit memos, bills, vendor credits, bank transactions and journal entries** have a location field, and a **Class** column on each line.
- **Tags** are ticked on the transaction's page (**Tags**, then **Save tags**). They can be changed at any time after posting.
- **Recurring templates** carry their classes, location and tags into every transaction they make.
- **Journal entry CSV imports** can include class, location and tag columns.

Voiding a transaction copies its classes, locations and tags onto the reversal, so voided work nets to zero in every report.

## Fixing them afterwards

Their classes and locations can be corrected here without changing the transaction itself or posting anything, because they're kept beside the ledger rather than in it.

**Classes and locations → Reclassify** (accountant):

1. Filter the lines: **from** and **to** dates, an **account**, and what the **class** or location is **now** (including "not specified").
2. **Show lines**, tick the ones to change.
3. Choose the new class and/or location and **Reclassify selected lines**.

The documents follow: an invoice's lines show the new class too, so cash basis reports agree. Each change is recorded in the audit log. Lines in a closed period need the closing date password.

## Reports

- **Profit and loss by class** and **by location** give a column per class or location, plus "Not specified" and a total.
- The **profit and loss**, **general ledger**, **sales by customer** and **expenses by vendor** reports have **class**, **location** and **tag** filters. Choosing a class or location includes the ones under it.
- **Budgets** can be made for one class or location (see [Budgets](20-budgets-and-analysis.md#budgets)).

These work on accrual and cash basis. On cash basis, a customer payment takes its classes from the invoice it paid.

For nonprofits, classes also carry the **functional area** (program, management and general, fundraising) and the purpose of restricted gifts; see [Nonprofits](23-nonprofits.md).
