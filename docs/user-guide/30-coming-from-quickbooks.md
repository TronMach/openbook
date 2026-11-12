# Coming from QuickBooks

If you've kept books in QuickBooks Desktop or QuickBooks Online for years, most of OpenBook will feel familiar: customers, invoices, Undeposited Funds, bills, checks, reconciling, classes and the closing date are all here. What changes is mostly *where* things are and a few habits, above all that nothing posted is ever deleted.

This chapter is a map from what you know to where it is in OpenBook. It isn't about bringing your data over: for that, see [Moving in from QuickBooks and other programs](24-moving-in.md).

Two things make the first days easier:

- **The simple view** has a menu bar by task (**Customers**, **Vendors**, **Banking** and so on) and a home page that draws the work as steps, Estimates → Create Invoices → Receive Payments → Make Deposits, much as QuickBooks Desktop's home page does. It's suggested for a business one person runs. Turn it on or off from your username in the top bar; see [The simple view](28-simple-view.md).
- **The full view** has a bar of areas (**Sales**, **Purchases**, **Banking**…). Each area's page has the common jobs at the top and the rest in groups. If you'd rather pick pages from lists, select your username, then **Show the menu as drop-downs**. See [Finding your way around](02-finding-your-way.md).

The paths below are for the full view, with the simple view's menu item where it has one.

## Where things are

### Customers and sales

| In QuickBooks | In OpenBook | Notes / chapter link |
| --- | --- | --- |
| Customer Center | **Customer Center** on the customer list; **Customers → Customer Center** in the simple view | The list and one customer side by side, with their transactions. [The simple view](28-simple-view.md#customer-vendor-and-item-centers) |
| New customer | **Sales → Customers → New customer** | [Customers and sales](04-sales.md) |
| Customer:Job | A sub-customer (**Sub-customer of** on the customer), or a project under **Projects** | A project is a sub-customer billed with its client. [Projects, time and mileage](18-projects-and-time.md) |
| Create Invoices | **Sales → New invoice** (simple view: **Customers → Create Invoices**) | [Customers and sales](04-sales.md) |
| Batch invoicing | **Sales → Batch of invoices** | [Customers and sales](04-sales.md) |
| Receive Payments | **Sales → Receive payment** | Deposits to Undeposited Funds unless you choose a bank account |
| Make Deposits / Undeposited Funds | **Banking → Make deposit** (simple view: **Banking → Make Deposits**) | The button shows how many payments are waiting. **Deposit slip** prints from the deposit's page |
| Sales Receipts | **Sales → Sales receipt** | [Customers and sales](04-sales.md) |
| Estimates | **Sales → Estimate** | Turned into an invoice whole, by percentage or by chosen quantities. [Estimates, orders, credits and recurring invoices](09-more-sales.md) |
| Sales orders | **Sales → Sales orders** | They post nothing and don't hold stock. [Chapter 9](09-more-sales.md) |
| Credit memos and refunds | **Sales → Credit memo**; **Refund** on the credit memo or payment | [Chapter 9](09-more-sales.md) |
| Customer deposits or retainers | **Sales → Retainers** | [Chapter 9](09-more-sales.md) |
| Memorized or recurring invoices | **Sales → Recurring invoices** | Automatic, or waiting for you to create them |
| Billable expenses and time | **Sales → Billable expenses**; unbilled time and costs are listed on the customer's new invoice | [Chapter 9](09-more-sales.md), [chapter 18](18-projects-and-time.md) |
| Statements | **Statement** on the customer's page; **Sales → Print** for many at once | [Collections](10-collections.md), [Printed forms](27-printed-forms.md) |
| Finance charges | Late fees, under **Sales → Collections** | Set up by an accountant. [Collections](10-collections.md) |
| Writing off a bad debt | **Write off** on the invoice, or **Sales → Collections → Write off bad debt** | [Collections](10-collections.md) |
| Price levels | **Sales → Pricing** | Price levels, quantity breaks, price rules and rounding. [Pricing](13-pricing.md) |
| Online invoice payments | **Settings → Online card payments** (Stripe) | [Getting paid online](11-online-payments.md) |
| Sales tax | **Sales → Sales tax**; **Pay** on an agency's row when you file | [Sales tax](12-sales-tax.md) |

### Vendors and purchases

| In QuickBooks | In OpenBook | Notes / chapter link |
| --- | --- | --- |
| Vendor Center | **Vendor Center** on the vendor list; **Vendors → Vendor Center** in the simple view | |
| Enter Bills | **Purchases → New bill** (simple view: **Vendors → Enter Bills**) | [Vendors and purchases](05-purchases.md) |
| Pay Bills | **Purchases → Pay bills** | Pays from a bank account (numbering checks) or a credit card |
| Write Checks / expenses | **Check / payment** on the account's row on **Banking** (simple view: **Banking → Write Checks**) | For something already paid. Use a bill *or* an expense for one purchase, never both |
| Credit card charges and credits | **Card charge** and **Card credit** on the card's row on **Banking** | |
| Paying the credit card bill | **Banking → Transfer** | From the bank account to the card |
| Vendor credits | **Purchases → Vendor credit** | **Apply to bills**, or record their refund |
| Memorized or recurring bills | **Purchases → Recurring bills** | |
| Purchase orders | **Purchases → Purchase order** | [Purchase orders, approvals and ACH payments](15-purchase-orders.md) |
| Receive items | **Receive goods** on the purchase order (simple view: **Inventory → Receive Inventory**) | See [What works differently](#what-works-differently) |
| Print checks | **Banking → Print checks** | [Bank connections, checks, positive pay and petty cash](21-banking-tools.md) |
| Bill approvals | **Purchases → Bill approvals** | Set up by an accountant. [Chapter 15](15-purchase-orders.md) |
| 1099s | **Track payments for a 1099** on the vendor; **Reports → 1099 summary** | Filing data, vendors' copies and W-9 requests are on the report. OpenBook doesn't file with the IRS. [Vendors and purchases](05-purchases.md) |

### Banking

| In QuickBooks | In OpenBook | Notes / chapter link |
| --- | --- | --- |
| Bank feeds: downloading transactions | **Import** on the account's row on **Banking** (OFX, QFX, QBO or CSV) | [Banking](06-banking.md) |
| Bank feeds: connected accounts | **Banking → Bank connections** (SimpleFIN, accountant role) | [Chapter 21](21-banking-tools.md) |
| For Review | **Review** (or **… to review**) on the account | Match, add, or exclude each line |
| Bank rules | **Banking → Bank rules** | Can split a line by percentage |
| Reconcile | **Reconcile** on the account | **Undo reconciliation** is on the latest reconciliation report |
| Registers | **Register** beside each account on **Accounts** (simple view: **Banking → Registers and Reconciling**) | [Your chart of accounts](03-chart-of-accounts.md) |
| Transfer Funds | **Banking → Transfer** | Shown once you have two bank or card accounts |
| Memorized expenses on a schedule | **Banking → Recurring expenses** | [Chapter 21](21-banking-tools.md) |

### Inventory

| In QuickBooks | In OpenBook | Notes / chapter link |
| --- | --- | --- |
| Item list | **Sales → Products and services** | Simple view: **Inventory → Item Center** |
| Service item | Type **Service** | |
| Non-inventory part | Type **Non-inventory product** | |
| Inventory part | Type **Inventory product** | FIFO cost unless you choose **Weighted average** on the item. [Inventory](14-inventory.md) |
| Group item | Type **Bundle (kit)** | Sold as one line at its own price; its parts leave stock |
| Inventory assembly | Type **Assembly**; **Inventory → Build assemblies** | Pending builds are kept until finished |
| Adjust quantity on hand | **Inventory → Adjust quantities** | |
| Physical inventory worksheet | **Inventory → Count sheet**, then **Count stock** | |
| Inventory sites | **Inventory → Warehouses and bins** | **Transfer stock** moves stock between them |
| Reorder reminders | **Purchases → Reorder stock** | Counts what's on order and what open sales orders still need |

### Reports

| In QuickBooks | In OpenBook | Notes / chapter link |
| --- | --- | --- |
| Report Center | **Reports** | [Reports](07-reports.md) |
| Memorized reports | **Save report** on any report | Saved reports are listed at the top of **Reports** |
| Scheduled or emailed reports | **Reports → Email reports on a schedule** | Accountant role. [Budgets, the dashboard, custom reports and accountant tools](20-budgets-and-analysis.md) |
| Custom transaction detail | **Reports → Build a custom report** (the **Custom transaction report**) | [Chapter 20](20-budgets-and-analysis.md) |
| Company Snapshot / business overview | **Dashboard** | [Chapter 20](20-budgets-and-analysis.md) |
| Budgets and Budget vs. Actual | **Reports → Budgets**; the **Budget vs. actual** report | Accountant role |
| Accrual or cash basis | **Basis** on each report | The default is in **Settings → Company details** |
| Find | **Search** in the top bar (or press `/`) | Finds names, numbers, memos and amounts. [Finding your way around](02-finding-your-way.md) |

### Company and lists

| In QuickBooks | In OpenBook | Notes / chapter link |
| --- | --- | --- |
| Company information | **Settings → Company details** | |
| Preferences | **Settings** | Grouped by what they're for, with **Find a setting** |
| Chart of accounts | **Accounts** | [Your chart of accounts](03-chart-of-accounts.md) |
| Classes and locations (and tags in QuickBooks Online) | **Accounts → Classes and locations** | Tracking is turned on there. [Classes, locations and tags](17-classes-locations-tags.md) |
| Custom fields | **Settings → Custom fields** | [Custom fields, notes and attachments](25-custom-fields-notes-attachments.md) |
| Users and roles | **Settings → Users and invitations** | Admin role. [People, backups and safety](08-users-and-backups.md) |
| Templates / Layout Designer | **Settings → Invoice and form designs** | Accountant role. [Printed forms](27-printed-forms.md) |
| Email templates | **Settings → Email messages** | [Email](29-email.md) |
| Back up company | **Settings → Backups** (simple view: **Company → Back Up Company**) | Admin role |
| Merging two names | **Merge into another customer…** on the duplicate's page (vendors and items the same way) | Accountant role. [Customers and sales](04-sales.md) |
| Time tracking and timesheets | **Projects → Time** | Timers, a week grid, review and billing runs. [Projects, time and mileage](18-projects-and-time.md) |
| Mileage | **Projects → Mileage** | [Chapter 18](18-projects-and-time.md) |
| Fixed Asset Manager | **Accounts → Fixed assets** | Depreciation posts itself. Accountant role to change. [Fixed assets and loans](19-assets-and-loans.md) |
| Loan Manager | **Accounts → Loans** | [Chapter 19](19-assets-and-loans.md) |

### Accountant tasks

| In QuickBooks | In OpenBook | Notes / chapter link |
| --- | --- | --- |
| Make General Journal Entries | **Journal → New entry** | Accountant role. Tick **Adjusting entry** for an adjustment |
| Memorized or recurring journal entries | **Journal → Recurring** | [Your chart of accounts](03-chart-of-accounts.md) |
| Opening balances | **Settings → Opening balances** | [Chapter 3](03-chart-of-accounts.md) |
| Closing date and password | **Settings → Closing date** (also **Accounts → Close the books**) | Accountant role. [People, backups and safety](08-users-and-backups.md) |
| Audit trail | **Settings → Audit log**, or **History** on a record | Accountant role |
| Working trial balance | The **Trial balance worksheet** report | [Chapter 20](20-budgets-and-analysis.md) |
| Reclassify transactions | **Accounts → Classes and locations → Reclassify transactions** | For classes and locations only |
| Year-end files for the accountant | **Accounts → Accountant tools → Year-end package** | A ZIP of the main reports as Excel files |

## What works differently

**Nothing posted is deleted.** There's no Delete on a transaction. To correct an invoice, payment, sales receipt, bill, bill payment, credit, expense, check, deposit or transfer, open it and use **Change**: say why, and OpenBook reverses the old entry on its original date and posts the corrected one, keeping the number, payments and attachments. To cancel something, **Void** it (with a reason), which records the opposite entry on the same date. Both the original and the correction stay on record. A journal entry someone typed in has **Change** too; entries made by a document are corrected through the document.

**Every change is in the audit log.** It can't be turned off, edited or cleared, even by an administrator, and each person signs in with their own username so the log says who did what.

**Lists are made inactive, never deleted.** Customers, vendors, items, accounts, classes and people stay on old transactions. A customer or vendor still owing or owed money can't be made inactive until that's settled.

**A reconciled transaction can't be voided or changed.** Undo the reconciliation first (from the account's latest reconciliation report), correct the transaction, and finish again.

**Receiving goods doesn't put stock on the books by default.** **Receive goods** on a purchase order records what arrived, to check the bill against; the stock enters the books with the bill. If you want received goods available to sell straight away, an accountant can turn on receiving into stock in the approval settings ([chapter 15](15-purchase-orders.md)).

**Stock doesn't go below zero** unless you allow it under **Stock** in **Settings → Company details**, or on the item. Enter the bill or an adjustment first.

**Sales tax uses different words.** A *rate* is one percentage paid to one *agency*; a *tax code* is what goes on a sale and can charge several rates together. Whether an item is taxable is a **Taxable** box on the item and on each line, not a code. See [Sales tax](12-sales-tax.md).

**Closing a year posts nothing.** The balance sheet carries past years' profit into retained earnings by itself, as you're used to. The closing date password is separate from sign-in passwords, and each change made with it is in the audit log.

**Cash or accrual is chosen per report.** Every report has its own **Basis**, starting from the company's default. Switching never changes the books.

**Access shows, greyed out.** A button your role doesn't allow stays on the page, greyed out, and says what it needs and whom to ask. The four roles are view-only, standard, accountant and admin, and administrators can make permission sets by area.

**Several people work at once.** There's no single-user mode to switch to. If two of you save the same record at the same moment, OpenBook either fits both in or tells you to reload.

**The edition and the view only change what's on screen.** The edition (general, professional services, e-commerce or nonprofit) decides which areas the menu leads with, and in some editions customers are called **Clients** or **Donors**. Neither the edition nor the simple view turns anything off or changes your books.

**Imported history is journal entries.** If you brought years of QuickBooks history in, those transactions are journal entries, not invoices and bills, so the aging reports only list the open documents you entered yourself. See [What to expect](24-moving-in.md#what-to-expect).

**Keyboard shortcuts are new.** Press `?` to see them: `n` then `i` starts an invoice, `g` then `b` goes to banking, `/` jumps to search, `Ctrl` + `Enter` saves.

### What isn't there

- **Payroll.** Run payroll in a payroll service and record its totals with a journal entry, or import them on **Settings → Import transactions**.
- **More than one currency.** A company keeps its books in its home currency, chosen at setup and fixed after that.
- **Several companies in one file.** Each company file (or PostgreSQL database) holds one company.
- **Opening a QuickBooks company file.** OpenBook can't read a .QBW itself; the QuickBooks export helper brings its contents over ([Moving in](24-moving-in.md)).
- **An Accountant's Copy.** Your accountant works in the same books instead: invite them on **Settings → Users and invitations** with the accountant role (and **Access ends on** for tax season, if you like), set the closing date so earlier periods stay as filed, and send the **Year-end package** from **Accountant tools** if they prefer files.

## Words that differ

| QuickBooks says | OpenBook says |
| --- | --- |
| Customer:Job, job | Sub-customer, or project |
| Item list | Products and services |
| Inventory part | Inventory product |
| Non-inventory part | Non-inventory product |
| Group item | Bundle (kit) |
| Inventory site | Warehouse (with bins) |
| Item receipt, receive items | Receive goods |
| Check, expense | Check / payment |
| Credit card charge | Card charge |
| For Review (bank feeds) | Review |
| Categorize a downloaded transaction | Add |
| Sales tax item | Rate |
| Sales tax group | Tax code |
| Sales tax vendor | Agency |
| Finance charge | Late fee |
| Memorized transaction | Recurring invoice, bill, expense or entry |
| Memorized report | Saved report |
| Company Snapshot | Dashboard |
| Preferences | Settings |
| Audit trail | Audit log |
| Delete a transaction | Void it (or **Change** it) |
| External accountant user | Accountant role |
