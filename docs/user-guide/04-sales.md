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

The customer page shows their invoices, payments and balance, and has a **Statement** you can print or email. Balances on the customer and vendor lists take off unused credit, so they match the aging reports and statements.

**Customer Center** on the customer list shows the list and one customer side by side, with every transaction they have; see [The simple view](28-simple-view.md#customer-vendor-and-item-centers). The products and services list has an **Item Center** button that does the same for items.

A customer with unpaid invoices or a recurring invoice still running can't be made inactive; once those are settled or stopped, **Make inactive** removes them from the pickers.

To add many at once, use **Customers → Import CSV** with a CSV file of names, emails, phones and addresses. As with every import, you get a preview first and nothing is added until every row is good. Customers that already exist are skipped.

### Merging duplicates

When the same customer was entered twice, open the one to get rid of and use **Merge into another customer…** at the bottom of its page: choose the customer to keep, tick the box to confirm, and **Merge**. Everything the duplicate had (invoices, payments, estimates, orders, credits, time, notes, files and custom field values) moves to the one kept, and the duplicate is made inactive with "(merged)" after its name. Amounts and accounts already posted don't change, and the audit log records the merge. It needs the accountant role and can't be undone.

A customer with sub-customers can't be merged until they're moved; two customers that each have something only one may have (each a project, say) can't be merged either. Vendors merge the same way from the vendor's page (bills, payments, expenses, purchase orders, and items that name it as their preferred vendor), and services and non-inventory products from the item's page (two items of the same type; inventory products, bundles and assemblies keep their own stock histories and can't be merged).

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

1. Choose the **customer**. Their terms fill in the **due date**, and their tax code is selected. Someone new? Type the name and choose **+ Add "name"**: a small box takes the name, email and address and you carry on with the invoice. Products and services work the same way on a line (for something you keep in stock, add it under **Items**, which asks about stock and costs). Bills, expenses and purchase orders do this for vendors.
2. The **invoice number** is suggested (the next in sequence); change it if you need to. Set the **invoice date**.
3. Add lines. For each, choose a **product or service**, then the **quantity** and **rate**. The rate fills in from the item's price, or from the customer's price level or a price rule when they apply; type a different rate to override it. A line with only a description and no item is a note.
4. Add **discount** lines if you need them: a percentage of the item lines above it, or a fixed amount.
5. Sales tax is calculated from the tax code and shown under the subtotal.
6. Optionally write a **message to customer**, which prints on the invoice.
7. **Save invoice** (or `Ctrl` + `Enter`).

If the connection drops while you're typing (a laptop asleep, the server restarted), the page comes back empty and offers **Bring it back**: what you'd entered is kept in that browser tab until you save, and goes when the tab closes. New invoices, sales receipts, estimates and bills do this.

If the customer has billable expenses, time or mileage waiting to be invoiced, they're listed on the form: tick the ones to add.

### Sending it

The invoice page has **PDF** (open or print it) and **Email**. Email opens the message beside a live preview of what the customer will see: change the wording, add copies, choose the design, send a test to yourself, then send it now or schedule it with **Send later**. See [Email](29-email.md). Before the first email, an administrator sets up **Settings → Email**: choose **Send with an SMTP server** and fill in your email provider's server details (the page lists them for Gmail and Microsoft 365; Gmail needs an *app password*), or **Save to a folder instead of sending** to try things out. **Send test** checks it works.

How the PDF looks (layout, logo, font, footer, and which customers or documents get which design) is set in **Settings → Invoice and form designs**; see [Printed forms](27-printed-forms.md).

### Fixing a mistake

**Change** at the top of an invoice opens it for correcting: the number, dates, lines, tax, location, warehouse and message. Say **Why are you changing it?** and **Save changes**. The invoice keeps its number and everything attached to it (payments, credits, emails, notes, files). Behind the scenes OpenBook reverses the invoice's journal entry on its original date and posts the corrected one, so reports read as if the invoice had always been right, and the journal still shows what it was. Each change is listed under **Changes** on the invoice, with who made it and why.

- The customer can't change: to bill someone else, void the invoice and enter a new one.
- If part of the invoice is paid, credited or written off, the new total can't be less than that.
- Lines billed from a sales order or estimate, and billable expenses, time and mileage, stay as they are (void the invoice to bill them again).
- Changing an invoice dated in a closed period needs the closing date password. So does changing which income an invoice paid in a closed period counts as, because cash-basis reports spread payments over the invoice's lines.

### Copying an invoice

**Copy** at the top of an invoice starts a new one with the same customer, lines, prices, tax, location and message, dated today with the next number. Change anything before saving. Billable expenses, time and mileage aren't copied (each is billed once), nor are lots of stock (they're picked again). Estimates, sales receipts and sales orders have **Copy** too.

To cancel an invoice instead, **Void this invoice** at the bottom of its page (give a reason). Voiding records an opposite entry on the same date, so your reports come out as if the invoice had never been entered, while the record of both stays. An invoice that has a payment or credit applied can't be voided until that payment is voided or the credit is removed (**Remove** beside the invoice on the credit memo's page; for credit left on a payment, change the payment). To reduce what a customer owes without changing the invoice, issue a **credit memo**.

## Batch invoicing

**Sales → Batch of invoices** makes the same invoice for several customers at once, such as a monthly fee or membership dues:

1. Choose the **invoice date**, an optional **message** and the lines. Leave a line's price on automatic to price it for each customer (their price level and any price rules); type a price to charge everyone the same.
2. Tick the **customers** (**Find** narrows the list; **Choose all shown** ticks them all).
3. **Preview** shows each customer's invoice: number, due date (from their terms), tax (from their tax code) and total.
4. **Make … invoices** makes them all. If any one can't be made (an inactive item, a closed period), none are, and the message says which customer.

Afterwards each invoice links to its page; **Print them** opens **Sales → Print**, where you can choose them by date.

## Receiving payments

When a customer pays an invoice, record it with **Receive payment**: on the invoice page, or **Sales → Receive payment** (`n` then `p`).

1. Choose the **customer**. Their open invoices are listed.
2. Enter the **amount received**. It's applied to the oldest invoices first, taking any early payment discount they qualify for. Change the amounts against each invoice if the customer said otherwise, or use **Pay in full** on a row.
3. Set the **payment date**, the **reference / check no.** and, if you like, the **payment method** (cash, check, card, bank transfer or other).
4. Choose where it goes in **Deposit to**:
   - **Undeposited Funds** (the default when your chart has it) if you'll take several payments to the bank together. They wait there until you make a deposit, below.
   - A **bank account** if the money went straight in, such as a bank transfer.
5. **Save payment**.

If the amount is more than the invoices you apply it to, the rest stays with the customer as a **credit**, which you can apply to a later invoice (**Apply credit** on the invoice) or refund.

To correct a payment, for example one applied to the wrong invoice, open it and use **Change**: the amount received, date, **Deposit to**, reference, memo and how much goes to each invoice (the customer stays). Say **Why are you changing it?** and **Save changes**. OpenBook reverses the payment's journal entry on its original date, posts the corrected one and settles the invoices again; each change is listed under **Changes** on the payment.

- Credit from the payment that you applied to an invoice later keeps its date.
- If part of the payment was refunded, at least that much has to stay unapplied.
- Once the payment is in a bank deposit, matched to a bank feed line or taken online, the amount and **Deposit to** account stay (and an online payment's reference too); how it's applied can still change.
- A payment reconciled at the bank can't be changed, as it can't be voided.

## Making deposits

If you received payments into Undeposited Funds, record the trip to the bank: **Banking → Make deposit** (the button shows how many are waiting).

1. Choose the bank account in **Deposit to** and the **date**.
2. Tick the payments that went into this deposit, so the total matches the deposit slip or the line on your bank statement.
3. **Save deposit**.

When the waiting payments were made different ways, **Tick only** buttons above the list tick just the cash, the checks or the card payments, since those usually reach the bank separately.

On a deposit's page, **Deposit slip** opens a PDF to take to the bank: each item with its method and check number, then the cash total, the check total with how many checks, and the deposit total. **Reports → Deposit detail** lists every deposit in a period with what went into it.

This way your bank account in OpenBook shows one deposit for the whole amount, just as your bank statement does, which makes reconciling easy.

To correct a deposit (a payment left out, or one that didn't go in after all), open it and use **Change**: tick or untick payments, change the date, account or memo, say **Why is it changing?** and **Save change**. Payments you untick go back to waiting. The deposit keeps its page, files and notes, and each change is listed under **Changes**. A reconciled deposit can't be changed, and one matched to a line from your bank feed keeps its account and total.

## Sales receipts

A *sales receipt* is for a sale paid on the spot: a shop sale, a card payment taken at the counter. It records the sale and the money together, with no invoice to pay later.

**Sales → Sales receipt** (`n` then `r`): choose the customer (optional for walk-in sales), add lines like an invoice, choose **Deposit to** (Undeposited Funds or a bank account) and **Save sales receipt**.

To correct a sales receipt, use **Change** at the top of its page: the customer (or none), number, date, deposit account, reference, lines, tax, location, warehouse and memo. Say **Why are you changing it?** and **Save changes**. As with invoices, OpenBook reverses the receipt's journal entry on its original date and posts the corrected one, and lists each change under **Changes** on the receipt.

- Once the receipt is in a bank deposit, its total and **Deposit to** account can't change (the deposit moved that amount); other changes are fine. Void the deposit first to change them.
- A receipt whose money is reconciled at the bank can't be changed, as it can't be voided.
- Stock sold on the receipt is put back and taken again, so there must be enough on hand for the corrected quantities.

## Where it shows up

- The **Sales** page lists recent invoices, payments, receipts and refunds; filter by kind with the links above the list. **Unpaid invoices** shows every unpaid one; the other lists show the newest 100, with **Show 100 more** below. For one customer's history, use the **Customer Center**.
- **Reports → A/R aging** shows who owes what and how overdue it is.
- The customer's page and **statement** show their account at any date.
- The **profit and loss** report shows the income.
