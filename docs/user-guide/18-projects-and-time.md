# Projects, time and mileage

For work you bill by the hour, or jobs whose profit you want to follow: record time and mileage against customers and projects, bill it on invoices, and see what each project earned and cost. In the professional services edition this comes first in the menu, and customers are called clients.

## Projects

A project in OpenBook *is* a customer record, usually a sub-customer of the client it's for ("Acme:Website redesign"). So invoices, payments and statements work for projects with nothing new to learn, and the client can be billed for all its projects together.

**Projects → New project**: choose the **customer** and give the **project name**. OpenBook creates the sub-customer, billed with the client and copying their terms, tax code and price level. Or turn an existing sub-customer into a project.

The project's page has its **status** (and dates) and **budgets** for revenue, cost and hours, then its time, expenses and mileage, with income, costs and what's still unbilled.

Once costs or hours reach 90% of their budget the page says so, and again when they go past it. A project with projects below it (phases set up as sub-projects of a job, say) also shows the figures with all of them added in. A **Completed** or **Cancelled** project takes no new time or trips; set it back to **In progress** first.

The **Projects and time** card (on the customer page and the project page, once people are set up) has two settings. **Responsible** names the partner or manager answering for a client or project. A project without its own takes its client's. **Project profitability** and **Unbilled work** have a **Responsible** choice, so each person can see their own clients' work. **Time on invoices** says how billed time shows on invoices addressed to that customer: **A line per entry** (date, person and notes; the usual), **A line per person**, **A line per service**, or **One sum per service** (the hours in its description). Summed lines add up to exactly what the entries would have billed. Every invoice with time on it also says how many **Hours** it bills.

## People and rates

**Time → People and rates** (`/time/people`) lists who records time:

- **Name**, **cost rate per hour** (what an hour of theirs costs you, for profitability; only accountants and admins see it, and only they can change this page) and **bill rate per hour** (what you charge, unless the rate card says otherwise). When you use classes, a **Class** for the person (their department) goes on the invoice lines their time is billed on, so income by class shows it.
- **Role**, for setting rates by role, such as Partner or Associate.
- **Hours a week**, for the utilization report.
- **OpenBook user**: link the person to their sign-in, so their time is entered as them.
- **Vendor**, for contractors you pay through bills.

### The rate card

**Time → Rate card** (accountant role to change) sets hourly rates for particular cases: for a client or every client, for a person, a role or a service, optionally from a start date. The most specific matching line wins:

1. for the client (then each parent up the tree, so projects inherit their client's rates): person, then role, then service, then the client alone;
2. for every client: person, then role, then service;
3. the person's own bill rate;
4. the service's price.

Among lines for the same thing, the latest that has started by the work's date wins. **Check** on the rate card answers which rate applies to a person, client, service and date.

For the new year, **New rates for the new year → Raise rates** adds, for every rate in effect on the date you choose, a new line from that date a percentage higher (tick **Round to whole dollars** if you like). Time keeps the rate it was entered at; to bring billable time that isn't invoiced yet up to the card, **Reprice unbilled time** from a date. Rates typed on an entry, and time written up or down, are left alone.

Time takes its rate when entered, and records where it came from ("Acme · Partner"). Changing the rate card afterwards doesn't reprice time already entered, until that entry is edited: saving one that took its rate from the card looks the rate up again.

## Recording time

**Time**, from the menu or a project's page:

- **Start timer**: choose the person, customer or project, service and notes, and start it. **Stop** when done; the minutes are rounded and saved. One timer runs per person at a time.
- **Add time**: the same fields with **hours** (to 2 decimals, up to 24 an entry) and a **date**.
- **Week grid**: a timesheet, one row per client and service, a column per day. Type hours in the cells and **Save the week**. Clearing a cell deletes its time. The blank rows at the bottom are for new work; one that repeats a row above adds its hours to that row. **Notes for time added** go on the time a row adds when you save (and onto the invoice with it); time already there keeps its own. **Copy last week's rows** starts the blank rows with last week's clients, services and billable settings, hours empty.

Billable time needs a customer or project and a service. A **rate per hour** typed in overrides the rate card. Time posts nothing to the books by itself; it's income only once invoiced. Time can be edited or deleted until it's invoiced.

**Missing time last week** on the Time page lists people with **hours a week** set (on **People and rates**) who entered fewer than that last week, each linking to their week grid. Once a period's hours are reviewed, **Lock time** (accountant role) through its last day: time dated then can't be added, changed or deleted any more, though it can still be billed, written down or repriced. Leave the date blank to unlock.

## Mileage

**Mileage** (`/mileage`) records trips: **date**, **miles**, **business purpose**, from, to, vehicle and driver. Add a **customer or project** and a **bill per mile** amount to bill it on.

The mileage log by year is your record for taxes. The IRS business rates are filled in at the bottom of the page; check them before filing, and an accountant can add later years as they're announced (**Set rate**).

## Billing time, mileage and expenses

When you create an invoice for a customer, their unbilled time, trips and billable expenses are listed on the form. Tick what to bill and they're added as lines: time at its service and rate, mileage as a "Mileage" line, expenses as "Billable Expenses".

An invoice for a client can include the work of all its projects billed with it. When the work belongs to more than one, the invoice groups the lines under a heading per project with a subtotal for each, on screen and on the PDF.

Voiding the invoice puts everything back on the unbilled list.

An invoice with a payment or credit on it can't be voided or have its time lines changed. To take some of its time back anyway, use **Take time back off this invoice** on the invoice's page: tick the entries, give the date and **Why**, and choose whether to **Write it off**. OpenBook makes a credit memo for that time at what it was billed (with the invoice's sales tax), applies it to what the invoice still owes (anything more is the customer's credit, for a refund or their next invoice), and the time either goes back with unbilled work or is written off.

## Project costs

Bill and expense lines can be charged to a project for its profitability without being billed to the client: in the **Billable to customers and project costs** panel on the bill or expense page, choose the project for the line and untick **Bill it**.

## The billing run

**Time → Billing run** (`/time/billing`) lists who has time, mileage and costs waiting to be billed through a date, with sub-customers billed with their client under the client. Tick clients and **Make drafts**. A draft posts nothing: open it to change what time is billed at, with a reason (0 writes it off), take lines off for a later bill, or add a message. **Save and post** makes the invoice; or tick several drafts on the run page and **Post ticked drafts**, emailing each if you like. **Discard** puts the work back.

**Realization** shows what billed time came to against what it was worth when worked, by client, and **Time written up or down** lists each change with its reason.

## Fixed fees and limits

On a project, **How it's billed** can be by the hour, a **fixed fee** or **not to exceed**. Time on a fixed-fee project stays out of unbilled work, since the fee is what's invoiced: invoice it as an ordinary line, and the project page sets the fee against the effort worked. A not-to-exceed project is billed by the hour, and the billing run warns when a bill would take it over.

## Reports

In the **Projects and time** group:

- **Project profitability**: income (including work billed on the client's own invoice), costs (marked expenses, time at cost rates, mileage at the year's rate) and margin per project.
- **Unbilled work**: time, mileage and expenses not yet on an invoice, per client, with the oldest work.
- **Unbilled work by age**: the same work per client, by how long ago it was done (0-30, 31-60, 61-90 and over 90 days).
- **Utilization**: per person, available hours, hours worked, the billable share, billable value and what was invoiced.
- **Time by customer** (one **Person**'s time, or the clients one person is **Responsible** for) and **Mileage log**.
