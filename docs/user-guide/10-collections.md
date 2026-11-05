# Collections: statements, reminders, late fees and bad debt

This chapter is about getting paid on time, and what to do when a customer doesn't pay.

## Who owes you what

- **Reports → A/R aging** lists each customer's unpaid balance split by how overdue it is: current, 1–30, 31–60, 61–90 and over 90 days. Run it as of any date. The detail version lists each invoice.
- **Reports → Open invoices** lists every unpaid invoice.
- A customer's page shows their invoices, payments and balance.

## Customer statements

A *statement* shows a customer their account over a period: the balance brought forward, each invoice, payment and credit with a running balance, and an aging of what's still open.

On the customer's page select **Statement**, choose **From** and **To**, then **Show**. **PDF** prints it; **Email** sends it with a message you can edit beside a preview ([Email](29-email.md)).

For a sub-customer billed with its parent, the statement is the parent's, and includes the sub-customer's invoices.

## The collections page

**Sales → Collections** shows what's due today:

- **Payment reminders due today**: tick the ones to send and **Send ticked reminders**.
- **Late fees due today**: tick the ones to charge and **Charge ticked fees**.
- **Bad debt written off**: past write-offs.

Reminders and late fees are off until an accountant sets them up under **Settings** on this page (also **Settings → Late fees and reminders**).

A late fee is an invoice of its own, listed on the page of the invoice it was charged on. To void an invoice with a late fee on it, void the fee first.

## Payment reminders

In **Collection settings → Payment reminders**:

- **When, in days from the due date**: a list such as `-3, 7, 30`, meaning 3 days before the due date, then a week late, then a month late (up to 6 steps). Each invoice gets each reminder once. If OpenBook misses a step (it was off, or the invoice was entered late), it skips to the latest step reached rather than sending an old one late.
- **Subject** and **Message**, beside a preview with sample values, with words in braces filled in for each invoice: `{customer}`, `{invoice}`, `{balance}`, `{due}`, `{days}` (days late), `{when}` ("is due on …" or "was due on …, 9 days ago") and `{company}`. Click one under **Fill in** to add it; the formatting buttons work as in [Email](29-email.md).
- **Send reminders automatically**: sent in the background every hour. Otherwise they wait on the collections page for you to review.

Each reminder emails the invoice as it stands, so it shows what's still owed: attached or as a link, as the customer is set to get documents, copied to their Cc addresses, in the invoice's design, with **Pay now** when online payments are on. Email needs to be set up first (**Settings → Email**, by an administrator).

## Late fees

In **Collection settings → Late fees**, tick **Charge late fees on overdue invoices** and set:

- **Percent of the overdue balance**, **plus a flat amount** (either can be zero).
- **Grace period**: days after the due date before a fee is charged.
- **Charge again every (days)**: 0 charges once per invoice; 30 is a monthly finance charge.
- **Charge them automatically**, or review them on the collections page.

Each fee is its own small invoice, due on receipt, for the "Late Fee" service (income goes to "Late Fee Income"). Both are created the first time a fee is charged. To waive a fee, void that invoice. OpenBook won't charge it again; with **Charge again every**, the next fee comes a full period after the one you waived.

To keep a customer out of reminders or late fees, tick **Don't send automatic payment reminders** or **Don't charge late fees** on their page.

## Writing off bad debt

When an invoice won't be paid, write it off so your income and receivables are right: **Sales → Collections → Write off bad debt** (or **Write off** on the invoice's page).

1. Find the invoices: **at least this many days past due**, **as of** a date, then **Show invoices**.
2. Tick the invoices. Leave the amount blank to write off the whole balance, or enter part of it.
3. Choose the **date** and the **expense account** ("Bad Debts" by default, created on first use).
4. **Write off ticked invoices**.

Each write-off posts an expense against accounts receivable and settles the invoice. If the customer pays after all, void the write-off from its page; the balance comes back and you can receive the payment.

Sales tax already charged on a written-off invoice stays owed in OpenBook. If your state allows a bad debt deduction, adjust it on your return.

On cash basis reports, write-offs don't appear at all: the unpaid income was never counted there in the first place.
