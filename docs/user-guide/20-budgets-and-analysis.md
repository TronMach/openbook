# Budgets, the dashboard, custom reports and accountant tools

[Reports](07-reports.md) covers the standard reports. This chapter covers planning and analysis: budgets, the dashboard, building your own reports, sending reports by email on a schedule, the cash-flow forecast, and the tools your accountant uses at year end.

## Budgets

A *budget* sets monthly amounts for income and expense accounts over one fiscal year. It posts nothing to the books.

**Reports → Budgets → New budget** (accountant role):

- **Name**, and the **fiscal year starting in** a month.
- **Start from**: **Blank**, **Last year's actual amounts, month by month**, or **A copy of another budget**.
- Optionally **for a class** or **for a location**, to budget one department, store or program.

**Create budget** opens a grid of accounts by month. Type the amounts. To spread a yearly figure evenly, type it under **Spread a year** and save: it replaces that row's months. **Save budget**.

The **Budget vs. actual** report shows actual income and expenses beside the budget, with what's over or under. A budget can be deleted; your books aren't affected.

## The dashboard

**Dashboard** in the menu shows the business at a glance, as of a date:

- **Position**: cash in the bank, what customers owe (and how much is overdue), what you owe vendors (and how much is overdue).
- **Profit**: income and net income for the month and fiscal year to date, against the same period last year, and gross margin.
- **Ratios**: current ratio, quick ratio and days sales outstanding (how long customers take to pay).
- **Income and expenses by month** and **cash at month end**, charts for the last twelve months, each with a table view.
- **Biggest expenses, year to date**.

It uses your company's default report basis. **Show** lets each person choose which panels they see; others aren't affected. The same figures are available as the **Key performance indicators** report.

## Building your own report

**Reports → Custom transaction report** (`/reports/builder`) lists ledger lines the way you want them, and ties to the financial statements on either basis.

- **Columns**: choose from date, number, type, name, account, memo, class, location, debit, credit, amount and running balance.
- **Group by** account, type, customer or vendor, month, class or location, with subtotals.
- **Filters**: an **account** (with its subaccounts), **accounts of class** (assets, income…), transaction **type**, **name contains**, **memo contains**, **amount from … to**, and a **custom field** value.
- **Title**, for when you save or print it.

Like every report, it can be exported, saved, and emailed on a schedule. It shows up to 5,000 lines; narrow the filters for more.

## Scheduled reports

Have a saved report emailed regularly, for example the profit and loss to the owners every month: **Reports → Scheduled reports** (accountant role).

1. Choose the **saved report** (save one first from its page with **Save report**).
2. **To**: up to 10 email addresses.
3. **How often**: **every day**, **every week on** a day, or **every month on day** 1–28, at an **hour** (in the company's time zone, set in **Settings**).
4. **Format**: PDF, Excel or CSV.
5. **Schedule**.

Reports go out in the background within the hour. If OpenBook was off at the time, a missed send goes once when it's back. **Send now** sends one straight away. Each send is recorded with its outcome.

## Cash-flow forecast

**Reports → Cash-flow forecast** looks ahead week by week: starting from today's bank balances, it adds open invoices on their due dates, subtracts open bills on theirs, and subtracts loan payments. Choose the start date (**from**) and how many **weeks** (up to 52), then **Run**.

**Scenarios** test "what if": a scenario can assume **customers pay** some days late and **bills are paid** some days late, and add items of your own, once, weekly or monthly, such as a new hire's salary or an equipment purchase. Pick a scenario in the forecast to see its effect. Scenarios post nothing.

## Accountant tools

**Accountant tools** (`/accountant`, accountant role) gathers what your accountant needs at year end:

- **Year-end package**: choose **from** and **to** and **Download package**: a ZIP of the main reports as Excel files (statements, trial balance, general ledger, aging, 1099 and more) with the 1099 data and a README. Send it to your accountant instead of exporting reports one by one.
- **1099 filing data**: a CSV for the **tax year** with each vendor over the threshold, their full tax ID, address, form and box, and amount, ready for a 1099 e-filing service. It contains full tax IDs, so handle it carefully.
- **Adjusting entries**: journal entries your accountant marks as *adjusting* (the **Adjusting entry** box on the journal entry form or page). The **Trial balance worksheet** report shows unadjusted balances, the adjustments and adjusted balances side by side, and **Adjusting journal entries** lists them.
