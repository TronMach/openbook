# Customers and sales

This chapter covers the everyday sales cycle: you sell something, send an invoice, the customer pays, and you take the money to the bank. Estimates, sales orders, credit memos, refunds and recurring invoices are in [chapter 9](09-more-sales.md), and collections in [chapter 10](10-collections.md).

Everything here starts from **Sales** in the menu.

## Customers

**Sales → Customers → New customer** (or press `n` then `c`).

- **Display name** is how the customer appears everywhere. It has to be unique.
- **Company, email, phone, billing and shipping address** print on invoices and statements. The email is where invoices are sent.
- **Payment terms** set the due date of their invoices: due on receipt, net 15, net 30 and so on. An **early payment discount** turns them into terms like "2% 10, Net 30": a customer who pays within 10 days may take 2% off.
- **Price level** gives this customer a pricing baseline (see [Pricing](13-pricing.md)).
- **Sub-customer of** puts a job, project or location under another customer, up to five levels. Tick **bill with parent** to have the parent billed and sent statements for it.
- **Tax code** and **exemption certificate** decide the sales tax on their invoices (see [Sales tax](12-sales-tax.md)).

The customer page shows their invoices, payments and balance, and has a **Statement** you can print or email.

A customer with unpaid invoices can't be made inactive; once they're settled, **Make inactive** removes them from the pickers.

To add many at once, use **Customers → Import CSV** with a CSV file of names, emails, phones and addresses. As with every import, you get a preview first and nothing is added until every row is good. Customers that already exist are skipped.

## Products and services

Every line on an invoice that charges something names a *product or service* (an *item*). The item sets the line's description, default price and the income account the sale goes to.

**Sales → Products and services → New** (or `n` then `t`):

- **Name**, **Description on sales forms** and **Price** (up to 4 decimal places; line totals round to the cent).
- **Type**:
  - **Service**: work you do (consulting hours, a repair).
  - **Non-inventory**: something you sell but don't count (a part bought for one job).
  - **Inventory**: something you stock and count. OpenBook tracks quantity on hand and its cost, and posts the cost of what you sell. See [Inventory](14-inventory.md).
  - **Bundle** and **Assembly**: items made of other items, also in [Inventory](14-inventory.md#bundles-and-assemblies).
- **Income account**: where sales of the item are credited, for example "Consulting Income" or "Product Sales".
- **Barcode** (optional): scanning it into any item picker chooses the item.
- **Taxable**: whether sales tax applies.

Items can be imported from CSV too.

## Invoices

An *invoice* records a sale the customer will pay for later. It increases what they owe you (accounts receivable) and records the income.

**Sales → New invoice** (or `n` then `i`):

1. Choose the **customer**. Their terms fill in the **due date**, and their tax code is selected.
2. The **invoice number** is suggested (the next in sequence); change it if you need to. Set the **invoice date**.
3. Add lines. For each, choose a **product or service**, then the **quantity** and **rate**. The rate fills in from the item's price, or from the customer's price level or a price rule when they apply; type a different rate to override it. A line with only a description and no item is a note.
4. Add **discount** lines if you need them: a percentage of the item lines above it, or a fixed amount.
5. Sales tax is calculated from the tax code and shown under the subtotal.
6. Optionally write a **message to customer**, which prints on the invoice.
7. **Save invoice** (or `Ctrl` + `Enter`).

If the customer has billable expenses, time or mileage waiting to be invoiced, they're listed on the form: tick the ones to add.

### Sending it

The invoice page has **PDF** (open or print it) and **Email**. Email sends the PDF to the customer's email address with a message you can edit, or schedule it for later with **Send later**. Before the first email, an administrator enters the mail server's details in **Settings → Email** (or chooses to save emails to a folder instead).

How the PDF looks (layout, logo, font, footer, and which customers or documents get which design) is set in **Settings → Invoice and form designs**; see [Printed forms](27-printed-forms.md).

### Fixing a mistake

Posted invoices can't be edited. Instead, **Void this invoice** at the bottom of its page (give a reason), then enter it again correctly. Voiding records an opposite entry on the same date, so your reports come out as if the invoice had never been entered, while the record of both stays.

An invoice that has a payment or credit applied can't be voided until that payment is voided or the credit unapplied. To reduce what a customer owes without voiding, issue a **credit memo**.

## Receiving payments

When a customer pays an invoice, record it with **Receive payment**: on the invoice page, or **Sales → Receive payment** (`n` then `p`).

1. Choose the **customer**. Their open invoices are listed.
2. Enter the **amount received**. It's applied to the oldest invoices first, taking any early payment discount they qualify for. Change the amounts against each invoice if the customer said otherwise, or use **Pay in full** on a row.
3. Set the **payment date** and the **reference / check no.**
4. Choose where it goes in **Deposit to**:
   - **Undeposited Funds** (the default when your chart has it) if you'll take several payments to the bank together. They wait there until you make a deposit, below.
   - A **bank account** if the money went straight in, such as a bank transfer.
5. **Save payment**.

If the amount is more than the invoices you apply it to, the rest stays with the customer as a **credit**, which you can apply to a later invoice (**Apply credit** on the invoice) or refund.

## Making deposits

If you received payments into Undeposited Funds, record the trip to the bank: **Banking → Make deposit** (the button shows how many are waiting).

1. Choose the bank account in **Deposit to** and the **date**.
2. Tick the payments that went into this deposit, so the total matches the deposit slip or the line on your bank statement.
3. **Save deposit**.

This way your bank account in OpenBook shows one deposit for the whole amount, just as your bank statement does, which makes reconciling easy.

## Sales receipts

A *sales receipt* is for a sale paid on the spot: a shop sale, a card payment taken at the counter. It records the sale and the money together, with no invoice to pay later.

**Sales → Sales receipt** (`n` then `r`): choose the customer (optional for walk-in sales), add lines like an invoice, choose **Deposit to** (Undeposited Funds or a bank account) and **Save sales receipt**.

## Where it shows up

- The **Sales** page lists recent invoices, payments, receipts and refunds; filter by kind with the links above the list.
- **Reports → A/R aging** shows who owes what and how overdue it is.
- The customer's page and **statement** show their account at any date.
- The **profit and loss** report shows the income.
