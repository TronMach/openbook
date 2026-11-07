# Reports

**Reports** in the menu lists every report, in groups (in the simple view it starts with a short list; **All reports** shows the rest, see [The simple view](28-simple-view.md#reports-and-the-dashboard)). This chapter covers the ones most businesses use and how all reports work. Budgets, the custom report builder, the dashboard and scheduled reports are in [chapter 20](20-budgets-and-analysis.md).

## The main reports

**Financial statements**

| Report | Answers |
| --- | --- |
| Profit and loss | Did we make money over a period? Income, cost of goods sold and expenses, with net income at the bottom |
| Balance sheet | What do we own and owe on a date? Assets, liabilities and equity |
| Statement of cash flows | Where did the cash come from and go over a period? Operating, investing and financing |
| Profit and loss by class / by location | The profit and loss with a column per class or location |

**Sales and customers**: Sales by customer, Sales by product or service, Open invoices, A/R aging (who owes you, by how overdue), Customer balance summary, Customer contact list, Product and service list, Open sales orders (what's still to invoice), Estimates by customer (with their status), Deposit detail (each bank deposit with the payments in it), Price list, Discounts given.

**Expenses and vendors**: Expenses by vendor, Unpaid bills, A/P aging (what you owe, by how overdue), Vendor balance summary, Vendor contact list, Open purchase orders (what's still to receive and bill), Received not billed (goods in from purchase orders the vendor hasn't billed yet, beside the Received Not Billed account).

**Inventory**: Inventory valuation, Inventory stock status, Physical inventory worksheet, Purchases by product (quantity, amount and average cost bought in a period).

**Taxes**: 1099 summary and detail, Sales tax liability, Tax summary (your accounts' totals by tax form line, ending at net profit; on Schedule C, half of meals comes off for the 50% limit, line 30 is there for the business use of your home, and the mileage log's standard mileage deduction is shown under **Not in the books**, for line 9 if you use the standard rate). For a partnership or S corporation, accounts can go on **Schedule K** lines (interest, dividends, capital gains, other income, section 179, charitable contributions), which stay out of page 1's ordinary business income; a **Meals** line takes half off on Forms 1065, 1120-S and 1120; and a **books to return** worksheet (Schedule M-1) shows how the net income per books becomes the income on the return. The balance sheet report is your starting point for Schedule L.

**Accountant**: Account list, Trial balance, General ledger, Journal, Trial balance worksheet, Adjusting journal entries.

The contact lists, the product and service list and the account list show active records; choose **Include inactive** to list the rest too. Exported to CSV or Excel, they're also the way to take a list into another program.

Other groups appear for projects and time, fixed assets and loans, and nonprofits.

## Choosing the period

Reports that cover a period have a **Period** list:

- This month, Last month, This quarter, Last quarter
- This fiscal year to date, This fiscal year, Last fiscal year (using the fiscal year start from setup)
- All dates
- Custom: enter **From** and **To**

Reports that show a moment in time, such as the balance sheet and aging, take an **As of** date instead. Select **Run report** after changing anything.

## Accrual or cash basis

**Basis** decides *when* income and expenses count:

- **Accrual**: income counts when you invoice, expenses when the bill arrives. This shows what you earned and owed in a period, whether or not money changed hands. Most accountants prefer it.
- **Cash**: income counts when the customer pays, expenses when you pay. This matches the money in your bank, and many small businesses file taxes this way.

Each report has its own basis choice; the default comes from **Settings**. Switching basis never changes your books, only how the report reads them. On cash basis, an invoice or bill that's been fully paid, in one payment or several, ends on its own figures for each account, after any credit applied. On cash basis, a payment that covers several invoice lines is split across their income accounts in proportion, so the profit and loss by account still adds up.

## Comparing periods

The profit and loss has **Compare**: previous period, same period last year, by month or by quarter. The balance sheet compares with a month earlier or a year earlier. Comparing with one other period adds **$ change** and **% change** columns; by month or quarter adds a total.

## Drilling down

Amounts in the statements are links. Select one to see the general ledger lines behind it, for that account and that column's dates, and from there each transaction. Names link to the customer, vendor or product, and document numbers to the invoice or bill.

## Printing, exporting and saving

Every report can be:

- **Printed** from the browser (**Print**); the page prints without the menus.
- **Exported** to **CSV**, **Excel** or **PDF**. Exports contain exactly what's on screen.
- **Saved** with **Save report**: give it a name and it keeps its settings (period, basis, comparison, filters). A relative period like "last month" stays relative, so a saved report always shows the latest last month. Saved reports are listed at the top of the Reports page. Saving under a name you already used updates that report.

Because a report's settings are in its address, you can also bookmark a report or send its link to a colleague who can sign in.

## When reports don't look right

- **Something is in the wrong account**: open it from the drill-down and use **Change** on the document. A journal entry someone typed in has **Change** too: the old entry is voided with your reason and the corrected one posted with the next number, together; entries posted by documents are changed through the document. Or ask your accountant to post a journal entry.
- **Balances look doubled**: the same purchase was probably entered as both a bill and an expense, or a customer payment as both a payment and a bank deposit. See [Vendors and purchases](05-purchases.md) and [Banking](06-banking.md).
- **Two reports disagree** (A/R aging and the balance sheet, say): **Accountant tools → Check the books** shows where, with links to the records involved. See [Checking the books](20-budgets-and-analysis.md#checking-the-books).
- **Opening Balance Equity has a balance**: that's normal after entering opening balances. Your accountant can move it into the right equity accounts.
