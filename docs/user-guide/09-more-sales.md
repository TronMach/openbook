# Estimates, orders, credits and recurring invoices

[Customers and sales](04-sales.md) covers the basic cycle of invoice, payment and deposit. This chapter covers the documents around it: quoting before the sale, taking orders, money received up front, corrections after the sale, and invoices that repeat.

| You want to… | Use |
| --- | --- |
| Quote a price the customer hasn't agreed to yet | Estimate |
| Record an order to deliver and bill later, perhaps in parts | Sales order |
| Bill a job in stages | Progress invoicing (from an estimate or sales order) |
| Take money before the work starts | Retainer |
| Reduce what a customer owes (a return, a goodwill discount) | Credit memo |
| Give money back | Refund |
| Bill the same thing every month | Recurring invoice |
| Charge for costs you paid on a customer's behalf | Billable expenses |

## Estimates

An *estimate* (quote) posts nothing to the books. It's a document you send, and later turn into an invoice.

**Sales → New estimate** (`n` then `e`): choose the customer, set the **date** and optionally **valid until**, add lines exactly as on an invoice, and **Save estimate**. **PDF** and **Email** work as for invoices.

An estimate's status is **Pending** until you mark it **Accepted** or **Declined** with the buttons on its page. After its valid-until date it shows as **Expired**. You can **Edit** it freely until it's invoiced.

If a customer accepted or declined an estimate online and you then change its customer, lines or total, it goes back to **Pending**: they answered a different offer. Changing only the date or message keeps their answer.

### Turning it into an invoice

On the estimate's page, **Invoice** offers three choices:

- **Everything that's left**: one invoice for the whole estimate. The estimate shows as **Invoiced** (closed).
- **This percentage of every line**: progress billing, for example 30% now. Each invoice bills that share of every line's quantity. An invoice made from an estimate prints where the contract stands: the estimate's total, what was billed before it, and what's left after it.
- **The quantities entered above**: bill only some lines, or part of a line's quantity.

Choose the **invoice date** and **Create invoice**. The estimate keeps track of what's been invoiced, and closes when everything has been. Invoices keep the estimate's prices, so later price changes don't affect them. Tax is worked out again on each invoice.

Once part of an estimate is invoiced it can't be edited any more. Voiding one of its invoices puts those quantities back to be invoiced again.

## Sales orders

A *sales order* records what a customer has ordered, with their PO number, before you deliver and bill it. Like an estimate, it posts nothing and doesn't reserve stock.

**Sales → Sales orders → New sales order**: customer, **order number** (its own sequence), **date**, **expected** delivery date, the **customer PO number** and the lines. The customer's PO number is copied into the memo of every invoice from the order ("Your PO …").

Invoice it from its page in the same three ways as an estimate: everything left, a percentage, or chosen quantities, for example only what you've shipped this week. The order is **Open** until everything is invoiced, then **Closed**. If the customer cancels the rest, use **Cancel the rest of the order**. An order can be edited until its first invoice.

## Retainers and deposits

A *retainer* is money a customer pays before you've invoiced anything. It isn't income yet: OpenBook holds it in a "Customer Deposits" liability account until you apply it to an invoice.

**Sales → Retainers → New** (**Receive a retainer**): customer, amount, date, and **deposit to** (a bank account, or Undeposited Funds to include it in a deposit later). **Receive retainer**.

An open invoice for that customer shows **Apply from a retainer** with each retainer they still hold and its balance: **Apply** pays the invoice from it (up to what the invoice owes) without leaving the invoice.

From the retainer's page you can:

- **Apply to an invoice**: pick one of the customer's open invoices and the amount. The invoice's balance goes down.
- **Refund what's left**, from a bank or card account.
- **Take back** an application or refund, or **Void this retainer** when nothing has been used.

## Credit memos

A *credit memo* is the opposite of an invoice: it reduces the customer's balance and your income. Use it for returns, allowances, or correcting an invoice that's already been paid.

**Sales → Credit memo**: choose the customer, add lines like an invoice (the products returned, or a line for the allowance), and **Save credit memo**. Credit memos share the invoice number sequence.

The credit then sits on the customer's account. Use it by either:

- **applying** it: on an invoice choose **Apply credit**, or **Sales → Apply credit** under Tools to pick the customer's credits and invoices (**Apply oldest first** does it in one step). Applying credit posts nothing new; it just settles the invoice. Applied to the wrong invoice? **Remove** beside it under **Applied to** on the credit memo takes it back: the invoice owes that amount again and the credit is free to use. Vendor credits work the same way.
- **refunding** it (below).

Overpayments work the same way: when a customer pays more than the invoices you apply the payment to, the rest is credit you can apply or refund.

To correct a credit memo, use **Change** at the top of its page: the number, date, lines, tax, location, warehouse and message (the customer stays). Say **Why are you changing it?** and **Save changes**. OpenBook reverses its journal entry on its original date and posts the corrected one, so what's applied and refunded stays with it; each change is listed under **Changes**.

- The new total can't be less than what's already applied and refunded, and the date can't be later than the day it was first applied.
- If it was refunded in a closed period, changing its lines needs the closing date password (the cash basis report spreads the refund over them).
- Returned stock is taken out and put back as corrected, which isn't possible once some of it has been sold again.

## Refunds

To pay a credit back, open the credit memo or the payment and choose **Refund**. Enter the amount (up to what's left), **paid from** (a bank or card account), the date and a check number or reference, then **Record refund**.

## Recurring invoices

For invoices that repeat, such as a monthly retainer fee, a subscription or rent you charge, set up a template once: **Sales → Recurring → New**.

- **Name** (for your own list), **customer**, and the lines.
- **Repeat every**: so many weeks, months, quarters or years.
- **First invoice date**, and optionally an **end date** or a number of **times**.
- **Create invoices automatically when due**: OpenBook creates each invoice on its date in the background. Leave it off to have due invoices wait for you on the Recurring page (**Create due invoices**), so you can check them first.
- **Email each invoice to the customer when it's created** (automatic templates).

Month-end dates stay at month end: a template that starts on 31 January bills on 28 or 29 February, then 31 March. Each invoice is taxed with the customer's tax code on its own date. Make a template inactive to stop it. **Make active** passes over the dates it missed; **Make active and catch up** makes them. Changing the schedule only affects what's still to come.

## Billable expenses

When you pay for something on a customer's behalf, such as materials for a job or a travel cost, you can pass it on to them.

1. Enter the bill or expense as usual.
2. On its page, the **Billable to customers and project costs** panel lists its lines. Choose the customer for a line, a markup percentage if you add one, and **Mark** it.
3. The cost waits on **Sales → Billable expenses**. When you next create an invoice for that customer, it's listed on the form; tick it to add it as a "Billable Expenses" line.

Voiding the invoice puts the costs back on the waiting list. A bill or expense whose cost has been invoiced can't be voided until that invoice is.

Billable time and mileage work the same way; see [Projects, time and mileage](18-projects-and-time.md#billing-time-mileage-and-expenses).
