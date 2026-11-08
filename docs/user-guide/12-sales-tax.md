# Sales tax

OpenBook works out sales tax on invoices, sales receipts, estimates and credit memos, keeps track of what you owe each tax agency, and records the payments when you file. Set it up on **Sales → Sales tax**.

## How it fits together

- An **agency** is who you pay: a state's department of revenue, a city, a county. Each agency gets its own liability account (under "Sales Tax Payable"), so you always know what you owe it.
- A **rate** is one percentage paid to one agency, for example "Texas state 6.25%".
- A **tax code** is what you put on a sale. It charges one or more rates together, for example "Austin" = state 6.25% + city 1% + transit 1%. Each rate makes a code of its own when you create it; add a code to combine rates.
- Each **customer** has a default tax code (where you deliver to them decides the rate), and may be **exempt**.
- Each **product or service** is taxable or not.

## Setting up

1. **Add an agency**: its **name**, how often **you file** (monthly, quarterly, twice a year or annually; this sets the period OpenBook suggests you report) your **permit or account number** with it, and how many days after a period ends its return is due (**Returns due**, 20 unless you change it).
2. **Add its rates**: **name**, **rate (%)** (up to four decimals) and **paid to** (the agency). When a rate changes on a set date (a state raising its rate on January 1), open the rate and add it under **Rate changes**: **From** the date and the new **Rate (%)**. Documents dated from then charge the new rate and earlier ones the old, even when entered later, so a back-dated invoice or credit memo gets the rate of its date. The tax shown while you fill in a form uses today's rate; the saved document uses its date's. Leave **Also make a tax code that charges just this rate** ticked unless you'll only use it combined with others.
3. If you charge several rates together, **add a tax code**, give it a name, and tick its rates. They print in the order they're listed there.
4. On each **customer**, choose their **tax code**. New invoices, receipts and estimates for them start with it.
5. On each **product or service**, tick **Taxable** if sales tax applies to it.

When a rate changes, edit its percentage. The new rate applies to documents from then on; existing ones keep the rate they were charged, even when you change them later: the tax is worked out again on the lines you change, at the rates and exemption the document had. Choosing a different tax code on the document uses that code as it stands now.

## On sales

When you choose a customer, their tax code is selected in the form's **Sales tax** field (it only appears once you have tax codes). Change it for one sale if you need to, for example delivery to a different place, or choose **No sales tax**. An exempt customer's form says so; if their certificate has expired, it warns you and tax is charged. Each line has a **taxable** box that starts from the product's setting.

OpenBook adds the tax under the subtotal: taxable lines, less their share of any discounts, times the code's combined rate, rounded once, then split among the rates. Invoices and PDFs show one tax row per rate.

### Exempt customers

On the customer's page, enter their **exemption certificate number**, the **reason** and the **expiry date**. While it's valid, their sales are taxed at zero and reported as exempt. Attach the certificate itself to the customer under **Attachments**. The Sales tax page lists every exempt customer.

A project or other sub-customer billed with its parent is covered by the parent's certificate.

## What you owe

The **Sales tax** page lists each agency with **Owed today** and the **period to file**. Once a period ends, the home page's to-do list shows **Sales tax returns to file** for each agency with tax owed for it, with the next due date (marked urgent once it's past), until the payment for that period is recorded.

**Reports → Sales tax liability** shows, for a period, each agency and rate: total sales, non-taxable, exempt and taxable amounts, and the tax. Use it to fill in your return. Credit memos are subtracted. **Selling into other states**: **Sales tax → Nexus thresholds** keeps each state's economic nexus threshold (the sales a year, and a number of transactions where the state has one) as you get them from the state or your adviser. **Reports → Sales by state** sets all your sales against them: invoices and receipts less credit memos, by the customer's shipping address (else billing), and sales channels by the state shipped to. A state past its threshold over the last twelve months with no tax agency set up for it shows on the home page's to-do list. OpenBook doesn't know the states' rules.

If your state lets you report on the **cash basis** (tax when you're paid), choose **Cash** for the basis: each invoice counts in proportion to what was paid on it in the period, sales receipts count on their date, credit memos count as they're refunded, and what's owed is what you've collected less what you've paid.

## Paying the tax

When you file, record the payment: **Pay** on the agency's row (or **Sales tax → Pay sales tax**). The amount starts at what's left to pay for the period, and a period with a payment recorded says **paid** on the Sales tax page.

1. Choose the **agency** and **for the period ending**. The amount starts as what was owed on that date.
2. Set the **payment date**, **paid from** (bank account) and **amount paid**.
3. Add adjustment lines if the return had them:
   - **Discount or credit**, such as a timely-filing discount the state lets you keep, to an income account.
   - **Penalty or interest**, to an expense account.
4. Add a **check number or confirmation** and **Record payment**.

The payment clears that much of the agency's liability. To correct one, void it from its page; the tax is owed again. Payments already reconciled at the bank can't be voided.

## Sales through channels

Tax that Amazon, Shopify and other sales channels report comes in with their payouts. To have it owed to the right agency, enter the state on the agency's page as the channel writes it (**State for online sales**, such as `TX`). When a payout posts, tax on lines shipped to that state goes to that agency's account: it shows as owed on **Sales tax** and in the liability report as **Online sales**, and paying the agency clears it. Tax a marketplace collected and paid itself nets to nothing there.

Tax with no state in the report, or for a state with no agency, stays in the channel's own sales tax account. **Sales tax by state (online orders)** shows the figures by state to file from.

## Turning things off

Rates, codes and agencies are made inactive, never deleted. A rate still used by an active code, or an agency with active rates, can't be made inactive until those are, and a code can't be while active customers use it: give them another code first. Changing a document that was charged under an inactive code keeps its tax.
