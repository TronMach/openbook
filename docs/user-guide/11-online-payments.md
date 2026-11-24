# Getting paid online and the customer portal

Customers can pay invoices online through Stripe, PayPal or Square, and see their account through a private portal link. Both are optional.

## Setting up online payments

You need an account with one of the three: [Stripe](https://stripe.com), [PayPal](https://developer.paypal.com) (a business account) or [Square](https://developer.squareup.com). An administrator then fills in **Settings → Online card payments**. Under **Take payments through**, choose the provider; the page then shows that provider's fields and its own **Setting up …, step by step** list, which ticks off each step that's done. Changing the provider first closes the links customers still hold from the old one (recording any that have been paid), then removes its saved keys. If a link can't be closed, the change waits and says why: close it in that provider's dashboard, then save again with **Change anyway** ticked.

For every provider:

- **Deposit payments to**: where payments are recorded, usually **Undeposited Funds**, so you can group them into one deposit matching the provider's payout to your bank (see [Making deposits](04-sales.md#making-deposits)).
- **Public address of OpenBook**: the address customers reach OpenBook at, for example `https://books.example.com`. Customers return here after paying, portal links start with it, and so do the webhook addresses below.
- **Take payments online**: the switch.

Keys are stored encrypted and never shown again; enter a new one to replace it.

### Stripe

- **Stripe secret key**: from Stripe's dashboard (Developers → API keys). A key starting `sk_test_` tries everything with Stripe's test cards first.
- **Webhook signing secret** (recommended): in Stripe's dashboard, add a webhook endpoint at your public address followed by `/hooks/stripe`, for the events `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.expired`, `payment_intent.succeeded` and `payment_intent.payment_failed` (and `charge.refunded` and `charge.dispute.created`, below), and paste its signing secret here. Stripe then tells OpenBook the moment a payment succeeds.

### PayPal

- **PayPal client ID** and **PayPal secret**: at developer.paypal.com, under **Apps & Credentials**, create an app (choose **Sandbox** to try it with PayPal's test accounts first, or **Live**) and copy both. Tick **Sandbox (test accounts, no real money)** for sandbox keys.
- **PayPal webhook ID** (recommended): in the app's settings, add a webhook at your public address followed by `/hooks/paypal`, for **Checkout order approved**, **Payment capture completed**, **Payment capture refunded** and **Customer dispute created**, and copy its ID here.

At PayPal the customer only approves a payment; OpenBook then takes it. That happens when the customer comes back to the thank-you page, when PayPal's webhook says they approved, or at OpenBook's own check every ten minutes. OpenBook never takes a payment for an amount the invoice no longer owes: the customer sees that nothing was charged and can pay what's owed now. PayPal drops an approval nobody takes within about three hours, so OpenBook needs to be running, and preferably reachable from the internet, while customers pay. On OpenBook Desktop, Stripe or Square suit better.

### Square

- **Square access token**: at developer.squareup.com, open your application and, under **Credentials**, copy the access token (switch to **Sandbox** to try it first, and tick **Sandbox (test cards, no real money)**). Payments go to your main Square location.
- **Webhook signature key** (recommended): under **Webhooks → Subscriptions**, add one at your public address followed by `/hooks/square`, for `payment.created`, `payment.updated`, `refund.created`, `refund.updated` and `dispute.created`, and paste its signature key here.

If OpenBook isn't reachable from the internet, you can skip the webhook. Payments are then recorded when the customer comes back to the thank-you page, or when you select **Check if paid** on the invoice.

Changing the provider, switching its **Sandbox** box or removing its key first closes the links customers still hold, and gifts under way on your giving page, at the old one. If one can't be closed, the page says so and offers **Change anyway**. Cards customers saved for automatic payment (below) can't be charged after that: their pages say so, and the customers have to agree again.

## How customers pay

Once online payments are on:

- **Emailed invoices** include a **Pay now** button. A link is for the balance when it was made: once the invoice is paid another way, changed or voided, OpenBook closes the link within a minute or two, so the customer can't pay the old amount. The next **Pay now** makes a link for what's owed then.
- An invoice's page has an **Online payment** panel: **Get a payment link for the balance** makes one to send any way you like, and **Check if paid** asks the provider about it. A PayPal link is an address on OpenBook, since PayPal's own page lasts only a few hours: it opens PayPal's page, or a new one for what's owed then.
- The **customer portal** (below) has **Pay now** on every open invoice.

The customer pays on the provider's own page; OpenBook never sees their card or PayPal sign-in. When the payment succeeds, OpenBook records an ordinary customer payment against the invoice (reference: the provider's payment ID), deposited to the account you chose. If they pay more than the balance, the rest becomes credit. A payment is never recorded twice, however many ways OpenBook hears about it.

The provider's fees aren't recorded automatically. When it pays out to your bank, make the deposit from Undeposited Funds with the payments it covers, and take the fee off it under **Fees, cash back and other money** (**Take off**, to your fees account), so the deposit is what arrived and matches the bank line.

### Refunding a payment

Open the payment. Under **Refunds at Stripe** (or PayPal, or Square), enter the **Amount** (up to what hasn't been refunded), the account it's **Paid from**, the **Date** and a **Reason** if you like (the customer may see it), and select **Refund at Stripe**. OpenBook has the provider give the money back, and records it: what's refunded comes off the invoices the payment paid, which are owed again (the last ones first, and only as far as the payment isn't unused credit already), and a refund is paid to the customer from the account you chose. The invoice is ticked **Don't charge automatically**, so a saved card isn't charged for it again by itself. If you'd rather the invoice stayed settled, give the customer a credit memo for it as well.

The refund is refused, and nothing changes, when the provider says no, or when the books couldn't record it (a closed period asks for the closing date password). If the provider doesn't answer, the refund shows **Waiting**, here and on the home page's to-do list: it may or may not have been made. **Try again** asks the provider first and never refunds twice; **It didn't go through** gives it up, once the provider says it never had it. You can't refund from here at a provider you no longer use: do it in its dashboard.

### Refunds and disputes made at your payment provider

A refund you give in the provider's dashboard, or a dispute a customer opens, doesn't change your books by itself: the invoice still shows paid. With the webhook sending refunds and disputes (the events listed above), OpenBook notices them: the invoice's **Online payment** panel says what happened, and the home page's to-do list shows **Refunds and disputes made at your payment provider**. Then record it:

- **A refund**: open the payment, use **Change** so the refunded part isn't applied to the invoice (it becomes the customer's credit), then **Refund** that credit, paid from the account the provider takes it from. If you'd rather keep the invoice paid and give money back for something else, a credit memo and its refund do the same.
- **A dispute**: if you win it, nothing changes. If you lose it, open the payment and record **The payment came back**, with the dispute fee as the bank's fee.

Then select **Mark as dealt with**. Nothing is posted until you do one of these, so a refund is never counted twice; if you also connect Stripe as a sales channel, record the refund one way only.

Refunds you make from OpenBook aren't listed there: they're in the books already.

> If you also connect Stripe as a sales channel, payments for invoices are recognized there and not counted as sales twice. See [Sales channels](22-sales-channels.md).

## Automatic payment with a saved card

With Stripe, customers can agree to have their invoices charged to a card when they're due. (PayPal and Square can't do this through OpenBook.)

On the customer's page, under **Automatic payment**, select **Make a link to copy**, or **Email the link to …**. The link opens their portal at a page that says what they agree to; a customer who has a portal link already finds **Set up automatic payment** there. They tick that they agree, and enter the card on Stripe's own page: no card number ever reaches OpenBook. Their page then shows the card (its kind, last four digits and expiry) and when they agreed. The customer can stop it at any time with **Stop** in the portal; you can with **Remove the card**. Either way Stripe forgets the card.

From then on, within about an hour of an invoice's due date (or straight away when it's due already), OpenBook charges the card for what's still owed and records an ordinary customer payment, with the usual payment receipt emailed if email is set up. An invoice that's been paid, changed or voided meanwhile is charged only what it then owes, or not at all. A payment link still open on it is closed first, so it can't be paid twice. An invoice a card or link has paid once isn't charged again by itself, even if that payment is refunded or voided later.

- To leave an invoice out, tick **Don't charge automatically** in its **Online payment** panel.
- To charge an open invoice now, select **Charge the saved card** there. The panel lists every charge.
- A **declined** card is tried again 3 and 5 days after the first time. After that the invoice waits on the home page's to-do list, under **Invoices whose saved card keeps being declined**: ask the customer for another card, or to pay another way. A bank that asks the customer to confirm a payment counts as a decline, since they aren't there to do it.
- If Stripe doesn't answer, the charge shows **Waiting**, and OpenBook asks Stripe about it within the hour before doing anything else, so nothing is charged twice.

A sub-customer billed with its parent is paid by the parent's card.

## The customer portal

A portal link is a private web address where one customer can see their account without signing in:

- their open invoices, with **Pay now** when online payments are on, and PDFs to download
- paid invoices from the last year
- estimates waiting for an answer, which they can open as a PDF and **Accept** (typing their name) or **Decline** (with a reason if they like), and those answered recently
- a statement of recent activity
- their saved card, with **Stop**, or **Set up automatic payment** (with Stripe)

On the customer's page, under **Customer portal**, select **Make a portal link**. **Copy it straight away: it's only shown once.** Send it to the customer.

Because anyone with the link can see that customer's account, share it only with them. If it might have leaked, select **Turn off** next to it and make a new one. The list shows when each link was last used.

A sub-customer billed with its parent sees the parent's account.

When a customer answers an estimate in the portal, the estimate's status changes to **Accepted** or **Declined** and its page says who answered, when, and what they wrote; the audit log records it as "portal:" and the customer's name. An expired estimate can't be accepted. If you change the status yourself afterwards, your choice stands and the customer's answer stays in the history.
