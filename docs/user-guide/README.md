# OpenBook user guide

OpenBook is open-source accounting for small businesses and nonprofits: invoices, bills, banking, inventory, projects, reports and the rest of what a business needs to keep its books, in your browser, with your data in a file or database you control.

This guide is for the people who use OpenBook day to day. It explains what each part does and how to get common jobs done. It assumes you know what an invoice and a bill are, but not that you're an accountant; where an accounting idea matters, it's explained where it comes up.

## Start here

1. [Installing and starting OpenBook](01-getting-started.md): running it, setting up your company, signing in.
2. [Finding your way around](02-finding-your-way.md): the menu, editions, search, keyboard shortcuts, dark mode, language.
3. [Your chart of accounts](03-chart-of-accounts.md): starter charts, account types, subaccounts, opening balances.

## Everyday work

4. [Customers and sales](04-sales.md): products and services, invoices, receiving payments, deposits, sales receipts.
5. [Vendors and purchases](05-purchases.md): bills, paying bills, expenses, vendor credits.
6. [Banking](06-banking.md): importing statements, reviewing transactions, bank rules, reconciling.
7. [Reports](07-reports.md): the main statements, accrual and cash basis, exporting and saving reports.

## Running the books

8. [People, backups and safety](08-users-and-backups.md): users and roles, closing the books, backups and restore, the audit log.

## More on sales

9. [Estimates, orders, credits and recurring invoices](09-more-sales.md): estimates and progress invoicing, sales orders, retainers, credit memos, refunds, recurring invoices, billable expenses.
10. [Collections](10-collections.md): statements, payment reminders, late fees, writing off bad debt.
11. [Getting paid online and the customer portal](11-online-payments.md): Stripe payments and private portal links.
12. [Sales tax](12-sales-tax.md): agencies, rates and codes, exempt customers, the liability report, paying the tax.
13. [Pricing](13-pricing.md): price levels, quantity breaks, price rules and rounding.

## Stock, purchasing and paperwork

14. [Inventory](14-inventory.md): inventory items, adjustments, warehouses and bins, lots and serials, bundles and assemblies, landed costs, labels.
15. [Purchase orders, approvals and ACH payments](15-purchase-orders.md): ordering, receiving, billing from a PO, the 3-way match, bill approvals, ACH files.
16. [The document inbox and receipt capture](16-document-inbox.md): uploading receipts and documents, reading them on the server or with Claude, turning them into transactions.

## Tracking the business

17. [Classes, locations and tags](17-classes-locations-tags.md): extra ways to slice your reports, reclassifying afterwards.
18. [Projects, time and mileage](18-projects-and-time.md): projects, people and the rate card, timers and timesheets, mileage, billing work.

## Planning and special cases

19. [Fixed assets and loans](19-assets-and-loans.md): the asset register, depreciation (straight-line, declining balance, MACRS), disposals, loans and their payments.
20. [Budgets, the dashboard, custom reports and accountant tools](20-budgets-and-analysis.md): budgets, the dashboard, the custom transaction report, scheduled reports, the cash-flow forecast, the year-end package.
21. [Bank connections, checks, positive pay and petty cash](21-banking-tools.md): SimpleFIN connections, printing checks, positive pay files, petty cash boxes.
22. [Sales channels](22-sales-channels.md): payouts from Shopify, Stripe, Amazon, eBay, Etsy and PayPal, orders and stock.
23. [Nonprofits](23-nonprofits.md): donor restrictions and releases, functional expenses, gifts and letters, pledges, grants.

## Moving in and extending

24. [Moving in from QuickBooks and other programs](24-moving-in.md): starting fresh or importing the history from QuickBooks, GnuCash, Xero, Wave or FreshBooks; transaction CSV imports.
25. [Custom fields, notes and attachments](25-custom-fields-notes-attachments.md): your own fields on records and documents, notes, files.
26. [Offline capture, the command line, the API and add-ins](26-offline-cli-api.md): the phone capture page, API keys, webhooks, the `openbook` command, add-ins.
27. [Printed forms: designs, fonts and printing](27-printed-forms.md): invoice and form designs with logos and layout, fonts, printing in batches and the print-later queue.

## A few things OpenBook always does

Knowing these makes the rest of the guide easier to follow:

- **Nothing posted is ever edited or deleted.** To correct a journal entry you *void* it, which records an opposite entry on the same date, and then enter it again. The original stays on record, marked voided. Invoices, customer and bill payments, sales receipts, credit memos, bills, vendor credits, expenses, checks, deposits and transfers can be changed in place: OpenBook voids the old entry and posts the corrected one for you, keeping the document's number and payments.
- **Every change is recorded.** Who did what and when is kept in the audit log, which accountants can open from Settings or from the **History** link on most record pages.
- **Everything balances.** Every transaction, whatever screen it came from, ends up as a balanced journal entry. The database itself refuses anything that doesn't add up.
- **Lists are never emptied.** Customers, vendors, products and accounts you no longer use are made *inactive* rather than deleted, so old transactions keep their names.
