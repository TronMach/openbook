# Glossary

The accounting words and OpenBook's own names used in this guide and on the pages, each with the chapter that covers it. On-screen labels are in **bold**.

## 0–9

**1099**: US forms for what you paid contractors and others. Tick **Track payments for a 1099** on a vendor, and the **1099 summary** totals their bill payments and expenses (not card payments). See [Purchases](05-purchases.md).

**3-way match**: for a bill from a purchase order, OpenBook compares what was ordered, received and billed, and holds the bill for approval when they differ. See [Purchase orders](15-purchase-orders.md).

## A

**Account**: one of the buckets money is sorted into, such as Checking, Rent or Consulting Income. Every transaction moves amounts between accounts. See [Chart of accounts](03-chart-of-accounts.md).

**Account type**: bank, accounts receivable, fixed asset, credit card, equity, income, expense and so on. It decides where the account shows on reports. See [Chart of accounts](03-chart-of-accounts.md).

**Accountant tools**: the page gathering the year-end work: **Check the books**, the year-end package, 1099 filing data and adjusting entries. See [Budgets and analysis](20-budgets-and-analysis.md).

**Accounts payable (A/P)**: what you owe vendors on bills. Bills add to it and bill payments clear it. See [Purchases](05-purchases.md).

**Accounts receivable (A/R)**: what customers owe you. Invoices add to it; payments, credits and write-offs take it down. See [Sales](04-sales.md).

**Accrual basis**: counting income when you invoice and expenses when the bill arrives. Any report can switch between accrual and cash basis without changing the books. See [Reports](07-reports.md).

**Accumulated depreciation**: the depreciation posted on a fixed asset so far, in its own account. Cost less this is the asset's *book value*. See [Assets and loans](19-assets-and-loans.md).

**ACH payment**: paying a vendor by direct deposit. After **Pay bills**, **Purchases → ACH payments** makes a file to upload to your bank. See [Purchase orders](15-purchase-orders.md).

**Add-in**: a package adding reports or import formats, installed in the server's `plugins` folder. It has full access to the books, so install only ones you trust. See [Capture, API and add-ins](26-offline-cli-api.md).

**Adjusting entry**: a journal entry marked **Adjusting entry**, such as a year-end correction, shown in its own column on the trial balance worksheet. See [Budgets and analysis](20-budgets-and-analysis.md).

**Aging**: unpaid balances split by how overdue they are: current, 1–30, 31–60, 61–90 and over 90 days. **A/R aging** is who owes you; **A/P aging** what you owe. See [Collections](10-collections.md).

**API key**: a key starting `obk_` that lets a program or the `openbook` command act as you, never beyond your role. It's shown once; **Revoke** stops it. See [Capture, API and add-ins](26-offline-cli-api.md).

**Applying credit**: using a customer's credit, from a credit memo or an overpayment, to settle an invoice; it posts nothing new. Vendor credits apply to bills the same way. See [More sales](09-more-sales.md).

**Approval**: a bill at or above the **Approval limit**, or held by the 3-way match, waits on **Purchases → Approvals** and can't be paid until approved. See [Purchase orders](15-purchase-orders.md).

**Area**: one part of the books, such as sales or banking, that a permission set or limited API key gives a level in. See [People and backups](08-users-and-backups.md).

**Assembly**: an item you build from components and then stock. **Build assemblies** takes the components out at cost and puts the assemblies in; a build can wait as pending. See [Inventory](14-inventory.md).

**Attachments**: files kept on a record, such as a receipt photo, stored inside the company file so backups include them. See [Fields, notes and attachments](25-custom-fields-notes-attachments.md).

**Audit chain**: links each audit record to the one before by a *fingerprint*, so a record changed or deleted later breaks the chain. See [Budgets and analysis](20-budgets-and-analysis.md).

**Audit log**: every change to the books: who, when and what, field by field. Nobody can edit or delete it, not even an administrator. See [People and backups](08-users-and-backups.md).

## B

**Backup**: a copy of the whole company file, attachments included, downloaded or made on a schedule. It doesn't hold the `keys` folder, so copy that too. See [People and backups](08-users-and-backups.md).

**Balance sheet**: what the business owns (assets), owes (liabilities) and is worth to its owners (equity) on a date. See [Reports](07-reports.md).

**Bank connection**: a live link to your bank through SimpleFIN Bridge; new transactions arrive in the review list every six hours. See [Banking tools](21-banking-tools.md).

**Bank feed review**: imported or fetched bank lines waiting on **Review**, each to match, add as a new transaction, pay to an open invoice or bill, or exclude. See [Banking](06-banking.md).

**Bank rule**: recognizes repeating bank lines by payee or memo and fills in the category, optionally a vendor or a split. The first matching rule wins. See [Banking](06-banking.md).

**Bill**: a vendor's request for payment you'll pay later. It records what you owe and the cost; **Pay bills** clears it. Use a bill or an expense for a purchase, never both. See [Purchases](05-purchases.md).

**Billable expense**: a cost paid on a customer's behalf and marked to pass on, with an optional markup. It's offered when you next invoice them. See [More sales](09-more-sales.md).

**Billing run**: **Time → Billing run** gathers unbilled time, mileage and costs through a date into draft invoices to check and post. See [Projects and time](18-projects-and-time.md).

**Books check**: **Check the books** confirms that agings, stock, undeposited funds, loans, reconciliations and the audit chain agree with the ledger. It runs daily and changes nothing. See [Budgets and analysis](20-budgets-and-analysis.md).

**Budget**: monthly amounts for income and expense accounts over a fiscal year. It posts nothing; **Budget vs. actual** compares. See [Budgets and analysis](20-budgets-and-analysis.md).

**Bundle**: an item sold as one line at its own price that takes its components out of stock, such as a gift basket. See [Inventory](14-inventory.md).

## C

**Cash basis**: counting income when the customer pays and expenses when you pay. It matches the bank, and many small businesses file taxes this way. See [Reports](07-reports.md).

**Cash-flow forecast**: a week-by-week look ahead from today's bank balances, with open invoices, bills and loan payments on their dates. See [Budgets and analysis](20-budgets-and-analysis.md).

**Category**: on a bill, expense or bank line, the account the money goes to. On a product or service, an optional grouping such as "Mugs". See [Purchases](05-purchases.md).

**Centers**: **Customer Center**, **Vendor Center** and **Item Center** show a list and one record side by side, with all its transactions. See [Simple view](28-simple-view.md).

**Change**: corrects a posted document or journal entry: OpenBook reverses the old entry on its original date and posts the corrected one, keeping the number and payments. See [Sales](04-sales.md).

**Chart of accounts**: the list of all your accounts, started from a starter chart for your legal structure. See [Chart of accounts](03-chart-of-accounts.md).

**Class**: an optional label on each line for departments, product lines or programs, so reports show figures per class. Classes nest up to five levels. See [Classes, locations and tags](17-classes-locations-tags.md).

**Closing date**: the last closed date, set on **Settings → Closing date**. Nothing dated on or before it can be entered, voided or changed; no closing entries are posted. See [People and backups](08-users-and-backups.md).

**Closing date password**: an optional password, separate from sign-in, that lets someone change a closed period. Each use is in the audit log. See [People and backups](08-users-and-backups.md).

**Command-line tool**: the `openbook` command, which works with a running server through the API using an API key; handy for scripts. See [Capture, API and add-ins](26-offline-cli-api.md).

**Company file**: the single file (`openbook.db`) holding a company's books and attachments, when it doesn't use PostgreSQL. See [Getting started](01-getting-started.md).

**Conditional grant**: a grant that's income only as its condition is met, such as cost reimbursement. Money received earlier is a refundable advance. See [Nonprofits](23-nonprofits.md).

**Cost of goods sold (COGS)**: the direct cost of what you sell. For stock, OpenBook moves the cost from "Inventory Asset" to Cost of Goods Sold as each sale is saved. See [Inventory](14-inventory.md).

**Credit memo**: the opposite of an invoice: it reduces what a customer owes and your income. It shares the invoice number sequence. See [More sales](09-more-sales.md).

**Custom field**: your own detail on products, customers, vendors or documents, such as a pack size; switched off, never deleted. See [Fields, notes and attachments](25-custom-fields-notes-attachments.md).

**Custom transaction report**: ledger lines with the columns, grouping and filters you choose. See [Budgets and analysis](20-budgets-and-analysis.md).

**Customer portal**: a private link where one customer sees their invoices, estimates and statement without signing in, and can pay. See [Online payments](11-online-payments.md).

## D

**Dashboard**: the business at a glance: cash, what's owed both ways, profit against last year, ratios and twelve months of charts. See [Budgets and analysis](20-budgets-and-analysis.md).

**Debit and credit**: the two sides of every journal entry line. Debits increase assets and expenses; credits increase liabilities, equity and income. See [Chart of accounts](03-chart-of-accounts.md).

**Deposit**: **Make deposit** takes payments waiting in Undeposited Funds to the bank as one amount matching the statement. See [Sales](04-sales.md).

**Depreciation**: spreading an asset's cost over the years it's used: straight-line, declining balance or MACRS. **Run depreciation** posts one entry for all assets. See [Assets and loans](19-assets-and-loans.md).

**Document inbox**: upload vendor invoices, customer purchase orders, packing slips and quotes; OpenBook reads them and makes the bill, order or delivery once you've checked. See [Document inbox](16-document-inbox.md).

**Donor**: what customers are called in the nonprofit edition; they're customer records. See [Nonprofits](23-nonprofits.md).

## E

**Economic nexus**: the sales level above which a state expects you to collect its tax. **Nexus thresholds** keeps each state's figure and **Sales by state** compares. See [Sales tax](12-sales-tax.md).

**Edition**: General, Professional services, E-commerce or Nonprofit. It changes only the menu order and home shortcuts; every feature stays available. See [Finding your way around](02-finding-your-way.md).

**Equity**: what the business is worth to its owners: capital, contributions and draws, retained earnings and opening balance equity. See [Chart of accounts](03-chart-of-accounts.md).

**Estimate**: a quote. It posts nothing; it's **Pending** until **Accepted** or **Declined**, then turned into invoices. See [More sales](09-more-sales.md).

**Expense**: a purchase paid on the spot (card charge, check, direct debit), recorded with **Check / payment** or **Card charge**. Also the account type for running costs. See [Purchases](05-purchases.md).

**Export all data**: a ZIP of every table as CSV plus every attachment, for analysis or moving elsewhere; not a backup you can restore. See [People and backups](08-users-and-backups.md).

## F

**FIFO (first in, first out)**: the default costing: a sale uses the oldest stock first, at what it cost. See [Inventory](14-inventory.md).

**Fingerprint**: the code linking each audit record to the one before. The year-end package's README keeps the latest, so a rewritten trail shows. See [Budgets and analysis](20-budgets-and-analysis.md).

**Fiscal year**: your financial year, starting in the month chosen at setup. Periods like **This fiscal year** and budgets follow it. See [Getting started](01-getting-started.md).

**Fixed asset**: equipment, a vehicle or a building that lasts for years, added to the asset register for depreciation. Selling or scrapping it is a disposal. See [Assets and loans](19-assets-and-loans.md).

**Form design**: how documents look as PDFs and emails: template, font, logo, columns and footer. See [Printed forms](27-printed-forms.md).

**Full view**: every area of OpenBook in the menu. Anyone can switch to or from the simple view; both work on the same books. See [Simple view](28-simple-view.md).

**Functional expenses**: a nonprofit's expenses by function: program services, management and general, and fundraising, carried by each class. See [Nonprofits](23-nonprofits.md).

## G

**General ledger**: every line posted to each account, with a running balance. See [Reports](07-reports.md).

**Gift**: a donation recorded with **Record a gift**, of money or in kind, to a contribution account and purpose; it gets a thank-you letter. See [Nonprofits](23-nonprofits.md).

**Grant**: an award from a funder, tracked by a class: spending on that class in the grant period counts against it. See [Nonprofits](23-nonprofits.md).

## H

**History**: the button on a record page showing every change to that record, who made it and what changed. See [Finding your way around](02-finding-your-way.md).

**Home currency**: the one currency the books are kept in, chosen at setup; it can't be changed. See [Getting started](01-getting-started.md).

## I

**Import batch**: the entries one move-in file brought. **Undo the whole import** voids them all at once, or none if one can't be. See [Moving in](24-moving-in.md).

**In-kind gift**: goods or services donated, recorded at fair value; the letter describes them without a value. See [Nonprofits](23-nonprofits.md).

**Inactive**: how accounts, customers, vendors, items and the like are retired: they leave the pickers but stay on old transactions. Nothing on a list is deleted. See [Chart of accounts](03-chart-of-accounts.md).

**Inventory adjustment**: **Adjust quantities** or **Count stock** after a count, breakage or theft; the value goes to the adjustment account ("Inventory Shrinkage" by default). See [Inventory](14-inventory.md).

**Invitation**: a one-use link letting a new person choose their own username and password, with the role or permission set you gave. See [People and backups](08-users-and-backups.md).

**Invoice**: a sale the customer pays later. It adds to accounts receivable and records the income. See [Sales](04-sales.md).

**Item**: a product or service on a line, setting its description, price and income account: service, non-inventory (sold but not counted), inventory, bundle or assembly. See [Sales](04-sales.md).

## J

**Journal entry**: debit and credit lines that balance. Every transaction ends up as one; **New journal entry** is for what no form covers. See [Chart of accounts](03-chart-of-accounts.md).

## K

**Keys folder**: holds the key that unlocks passwords saved in the books, such as email and bank connections. Backups don't include it: copy it somewhere safe. See [People and backups](08-users-and-backups.md).

## L

**Landed cost**: freight, duty and customs added to the cost of stock, spread by value or quantity; the part for stock already sold goes to cost of goods sold. See [Inventory](14-inventory.md).

**Late fee**: a charge on an overdue invoice after a grace period, made as a small invoice of its own; void it to waive it. See [Collections](10-collections.md).

**Loan**: a debt repaid monthly. Each payment is split into interest and the part that pays the loan down. See [Assets and loans](19-assets-and-loans.md).

**Location**: an optional label on a whole transaction, for stores, offices or regions; you can rename the word. See [Classes, locations and tags](17-classes-locations-tags.md).

**Lot and serial numbers**: a lot is a batch, often with an expiry date; a serial number is one unit. Tracked items name them when received and sold. See [Inventory](14-inventory.md).

## M

**MACRS**: the US tax depreciation tables: half-year convention for 3- to 20-year classes, mid-month for 27.5- and 39-year real estate, salvage ignored. See [Assets and loans](19-assets-and-loans.md).

**Merge**: moving everything a duplicate customer, vendor or item had onto the one kept; the duplicate becomes inactive. It can't be undone. See [Sales](04-sales.md).

**Mileage**: business trips with date, miles and purpose, to bill per mile or keep for taxes. See [Projects and time](18-projects-and-time.md).

## N

**Net assets**: a nonprofit's equity, without and with donor restrictions. See [Nonprofits](23-nonprofits.md).

**Net income**: income less cost of goods sold and expenses for a period, at the foot of the profit and loss. See [Reports](07-reports.md).

## O

**Offline capture**: the capture page (`/capture.html`) for entering time, mileage, expenses and invoices on a phone, kept on the device until it's online. See [Capture, API and add-ins](26-offline-cli-api.md).

**OFX, QFX and QBO**: bank statement download formats, the most reliable to import; CSV works too. See [Banking](06-banking.md).

**OpenBook Desktop**: the install for one Windows computer: a company file in Documents, a daily backup and a tray icon. See [People and backups](08-users-and-backups.md).

**OpenBook Server**: the install for several people, on a server or in Docker, usually with PostgreSQL. Desktop moves to it in one step. See [People and backups](08-users-and-backups.md).

**Opening balance equity**: the equity account taking whatever doesn't balance in opening balances; a balance there is normal. See [Chart of accounts](03-chart-of-accounts.md).

**Opening balances**: balances brought from your old books as of the day before you start. Leave A/R and A/P out and enter unpaid invoices and bills one by one. See [Chart of accounts](03-chart-of-accounts.md).

**Owner's draw and contribution**: equity accounts for money an owner takes out or puts in. They aren't expenses or income, so profit doesn't change. See [Banking](06-banking.md).

## P

**Payment**: a customer's money against invoices, recorded with **Receive payment**, into Undeposited Funds or a bank account. Any extra stays as credit. See [Sales](04-sales.md).

**Payment reminder**: an email a set number of days before or after an invoice is due, sent automatically or after review. See [Collections](10-collections.md).

**Payment terms**: how long a customer or vendor has to pay (net 30 and so on), which sets the due date. An early payment discount makes terms like "2% 10, Net 30". See [Sales](04-sales.md).

**Payout**: what a sales channel pays your bank after its fees, posted as one entry split into sales, shipping, tax, refunds and fees. See [Sales channels](22-sales-channels.md).

**Permission set**: a level (none, view only, standard or accountant) for each area of the books, given instead of a role. Admin things still need the admin role. See [People and backups](08-users-and-backups.md).

**Petty cash box**: a cash box kept at a fixed *float*. Spending is an ordinary expense; a count records differences to "Cash Over and Short", and **Replenish** tops it up. See [Banking tools](21-banking-tools.md).

**Pledge**: a promise to give: income when made on the accrual basis, when paid on the cash basis. It's paid by gifts that name it. See [Nonprofits](23-nonprofits.md).

**Positive pay**: you send the bank a file of the checks you've issued, and it refuses any check not on it. See [Banking tools](21-banking-tools.md).

**Posted**: in the books as a balanced journal entry. Posted entries are never edited or deleted: they're corrected with **Change** or cancelled with **Void**. See [the guide's introduction](README.md).

**PostgreSQL**: a database server that can hold the books instead of the company file, for a team. Back it up with PostgreSQL's own tools. See [Getting started](01-getting-started.md).

**Price level**: a customer's usual pricing, a percentage on everything or a price per item. See [Pricing](13-pricing.md).

**Price rule**: a promotion or exception for chosen items, customers, dates or quantities, applied lowest priority number first. See [Pricing](13-pricing.md).

**Print later**: puts a document in the print queue; **Sales → Print** prints the ticked ones as one PDF. See [Printed forms](27-printed-forms.md).

**Profit and loss**: income, cost of goods sold and expenses over a period, ending in net income. See [Reports](07-reports.md).

**Progress invoicing**: billing an estimate or sales order in stages, by a percentage of every line or chosen quantities. See [More sales](09-more-sales.md).

**Project**: a customer record, usually a sub-customer of the client, with a status, budgets, time and costs. See [Projects and time](18-projects-and-time.md).

**Purchase order (PO)**: an order you send a vendor. It posts nothing; deliveries and the bill are recorded against it. See [Purchase orders](15-purchase-orders.md).

**Purchase Price Variance**: the account for the difference between the cost goods came in at and what the vendor billed or credited. See [Purchase orders](15-purchase-orders.md).

## Q

**Quantity break**: a lower price on an item from a quantity on the document; it never raises the price. See [Pricing](13-pricing.md).

## R

**Rate card**: hourly bill rates for particular clients, people, roles or services; the most specific matching line wins. See [Projects and time](18-projects-and-time.md).

**Receipt capture**: **Purchases → Receipts**: upload receipt photos or PDFs, OpenBook reads them, and you record each as an expense. See [Document inbox](16-document-inbox.md).

**Received Not Billed**: the account holding goods received into stock from a purchase order until the vendor's bill clears it. See [Purchase orders](15-purchase-orders.md).

**Reclassify**: changing the class or location on posted lines without changing the transaction or posting anything. See [Classes, locations and tags](17-classes-locations-tags.md).

**Reconciliation**: ticking your books against a statement until the difference is zero. A reconciled transaction can't be voided; **Undo reconciliation** reopens one if needed. See [Banking](06-banking.md).

**Recurring template**: makes the same invoice, bill, expense, journal entry or gift on a schedule, by itself or when you choose to make what's due. See [More sales](09-more-sales.md).

**Refund**: paying a customer's credit back from a bank or card account. See [More sales](09-more-sales.md).

**Region**: how dates and numbers are written. Yours sets your screens; the company's **Region for documents** sets what's printed and emailed. See [Finding your way around](02-finding-your-way.md).

**Register**: every transaction that touched one account, with a running balance. See [Chart of accounts](03-chart-of-accounts.md).

**Restore**: replacing the books with a backup. The file is checked first and the current books saved as a safety copy. See [People and backups](08-users-and-backups.md).

**Restricted net assets**: money donors said may be spent only on a purpose or after a date, kept on accounts ticked **Holds amounts with donor restrictions**. Spending on the purpose lets you release it. See [Nonprofits](23-nonprofits.md).

**Retained earnings**: past years' profits. No closing entries are posted: the balance sheet carries each year's net income into it. See [People and backups](08-users-and-backups.md).

**Retainer**: money a customer pays before anything is invoiced, held in "Customer Deposits" (a liability) until applied or refunded. See [More sales](09-more-sales.md).

**Returned payment**: a payment the bank sent back, such as a bounced check. It isn't voided: **The payment came back** takes it out of the bank and makes the invoices owed again. See [Sales](04-sales.md).

**Reversal**: an entry with another's debits and credits swapped. Voiding posts one on the same date; **Reverse on** on a journal entry posts one on a date you choose. See [Chart of accounts](03-chart-of-accounts.md).

**Role**: view-only, standard, accountant or admin. Buttons a role can't use are greyed out, not hidden. See [People and backups](08-users-and-backups.md).

## S

**Sales channel**: an online shop or marketplace, such as Shopify or Amazon, whose payouts, orders and returns come in instead of an invoice per sale. See [Sales channels](22-sales-channels.md).

**Sales order**: a customer's order before you deliver and bill it. It posts nothing and doesn't reserve stock. See [More sales](09-more-sales.md).

**Sales receipt**: a sale paid on the spot, recording the sale and the money together. See [Sales](04-sales.md).

**Sales tax agency**: who you pay sales tax to. Each has its own liability account under "Sales Tax Payable". See [Sales tax](12-sales-tax.md).

**Sales tax code**: what you put on a sale: one or more rates charged together. Each customer has a default, and an exempt customer is taxed at zero while their certificate is valid. See [Sales tax](12-sales-tax.md).

**Section 179 and bonus depreciation**: part of an asset's cost written off in the month it's placed in service, the rest spread by its method. See [Assets and loans](19-assets-and-loans.md).

**Simple view**: a short menu bar by task and a home page that walks through each step, for a business one person runs. It changes only the screen, not the books. See [Simple view](28-simple-view.md).

**Single sign-on**: signing in with a Microsoft, Google or other OpenID Connect account; only people OpenBook knows or has invited get in. See [People and backups](08-users-and-backups.md).

**SKU**: a sales channel's product code, mapped to one of your items before its stock can be posted. See [Sales channels](22-sales-channels.md).

**Statement**: a customer's account over a period: balance brought forward, each invoice, payment and credit, and an aging. See [Collections](10-collections.md).

**Statement of activities**: a nonprofit's profit and loss, without and with donor restrictions. See [Nonprofits](23-nonprofits.md).

**Statement of cash flows**: where cash came from and went over a period: operating, investing and financing. See [Reports](07-reports.md).

**Statement of financial position**: a nonprofit's balance sheet, net assets split by restriction. See [Nonprofits](23-nonprofits.md).

**Straight-line**: depreciation of the same amount every month over the useful life, down to the salvage value. See [Assets and loans](19-assets-and-loans.md).

**Sub-customer**: a job, project or location under another customer; with **bill with parent**, the parent is billed. See [Sales](04-sales.md).

**Subaccount**: an account under another of the same type, such as "Utilities:Electric". See [Chart of accounts](03-chart-of-accounts.md).

## T

**Tag**: a flat label, several to a transaction, for events or campaigns; tags can change after posting. See [Classes, locations and tags](17-classes-locations-tags.md).

**Tax line**: where an account's total goes on your tax form (Schedule C, 1065, 1120-S, 1120 or 990); **Tax summary** adds them up. See [Chart of accounts](03-chart-of-accounts.md).

**To do**: the home page list of what's waiting: overdue invoices, deposits, bills, bank lines, figures that don't agree. See [Finding your way around](02-finding-your-way.md).

**Transfer**: moving money between your own accounts, or paying a card from the bank; it isn't income or an expense. See [Banking](06-banking.md).

**Trial balance**: every account's balance in debit and credit columns, which always come to the same total. See [Reports](07-reports.md).

**Two-factor sign-in**: your password plus a 6-digit code from an authenticator app; recovery codes cover a lost phone. See [People and backups](08-users-and-backups.md).

## U

**Unbilled work**: billable time, mileage and expenses not yet invoiced, offered on the next invoice and gathered by the billing run. See [Projects and time](18-projects-and-time.md).

**Undeposited Funds**: the holding account where payments, receipts and gifts wait for **Make deposit**, so the bank shows one deposit, as the statement does. See [Sales](04-sales.md).

## V

**Vendor**: someone you buy from, with terms, a default category and, for contractors, 1099 tracking. See [Purchases](05-purchases.md).

**Vendor credit**: reduces what you owe a vendor, applied to their bills or refunded by them. See [Purchases](05-purchases.md).

**Vendor deposit**: money paid before a vendor's bill, held in "Vendor Deposits" (an asset) until used on their bills. See [Purchases](05-purchases.md).

**Void**: cancels a posted transaction with an opposite entry on the same date; both stay on record. A reconciled transaction can't be voided. See [the guide's introduction](README.md).

## W

**Warehouse**: a place stock is kept, with optional bins for shelves. Everyone starts with "Main". See [Inventory](14-inventory.md).

**Webhook**: a signed message OpenBook sends another system when something changes, set up on **Settings → Webhooks**. See [Capture, API and add-ins](26-offline-cli-api.md).

**Week grid**: a timesheet of rows per client and service and a column per day; a week can be submitted for review. See [Projects and time](18-projects-and-time.md).

**Weighted average**: per-item costing at the average of what's on hand, instead of FIFO. See [Inventory](14-inventory.md).

**Write-off**: settling an unpaid invoice as bad debt, an expense against accounts receivable; void it if they pay after all. On the cash basis it doesn't appear. See [Collections](10-collections.md).

## Y

**Year-end package (accountant's package)**: a ZIP of the main reports as Excel files, with 1099 data and a README holding the audit fingerprint, for your accountant. See [Budgets and analysis](20-budgets-and-analysis.md).
