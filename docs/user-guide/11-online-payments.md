# Getting paid online and the customer portal

Customers can pay invoices online through Stripe, PayPal or Square, and see their account through a private portal link. Both are optional.

## Setting up online payments

You need an account with one of the three: [Stripe](https://stripe.com), [PayPal](https://developer.paypal.com) (a business account) or [Square](https://developer.squareup.com). An administrator then fills in **Settings → Online card payments**. Under **Take payments through**, choose the provider; the page then shows that provider's fields and its own **Setting up …, step by step** list, which ticks off each step that's done. Changing the provider removes the old provider's saved keys.

For every provider:

- **Deposit payments to**: where payments are recorded, usually **Undeposited Funds**, so you can group them into one deposit matching the provider's payout to your bank (see [Making deposits](04-sales.md#making-deposits)).
- **Public address of OpenBook**: the address customers reach OpenBook at, for example `https://books.example.com`. Customers return here after paying, portal links start with it, and so do the webhook addresses below.
- **Take payments online**: the switch.

Keys are stored encrypted and never shown again; enter a new one to replace it.

### Stripe

- **Stripe secret key**: from Stripe's dashboard (Developers → API keys). A key starting `sk_test_` tries everything with Stripe's test cards first.
- **Webhook signing secret** (recommended): in Stripe's dashboard, add a webhook endpoint at your public address followed by `/hooks/stripe`, for the events `checkout.session.completed`, `checkout.session.async_payment_succeeded` and `checkout.session.expired` (and `charge.refunded` and `charge.dispute.created`, below), and paste its signing secret here. Stripe then tells OpenBook the moment a payment succeeds.

### PayPal

- **PayPal client ID** and **PayPal secret**: at developer.paypal.com, under **Apps & Credentials**, create an app (choose **Sandbox** to try it with PayPal's test accounts first, or **Live**) and copy both. Tick **Sandbox (test accounts, no real money)** for sandbox keys.
- **PayPal webhook ID** (recommended): in the app's settings, add a webhook at your public address followed by `/hooks/paypal`, for **Checkout order approved**, **Payment capture completed**, **Payment capture refunded** and **Customer dispute created**, and copy its ID here.

At PayPal the customer only approves a payment; OpenBook then takes it. That happens when the customer comes back to the thank-you page, when PayPal's webhook says they approved, or at OpenBook's own check every ten minutes. OpenBook never takes a payment for an amount the invoice no longer owes: the customer sees that nothing was charged and can pay what's owed now. PayPal drops an approval nobody takes within about three hours, so OpenBook needs to be running, and preferably reachable from the internet, while customers pay. On OpenBook Desktop, Stripe or Square suit better.

### Square

- **Square access token**: at developer.squareup.com, open your application and, under **Credentials**, copy the access token (switch to **Sandbox** to try it first, and tick **Sandbox (test cards, no real money)**). Payments go to your main Square location.
- **Webhook signature key** (recommended): under **Webhooks → Subscriptions**, add one at your public address followed by `/hooks/square`, for `payment.created`, `payment.updated`, `refund.created`, `refund.updated` and `dispute.created`, and paste its signature key here.

If OpenBook isn't reachable from the internet, you can skip the webhook. Payments are then recorded when the customer comes back to the thank-you page, or when you select **Check if paid** on the invoice.

## How customers pay

Once online payments are on:

- **Emailed invoices** include a **Pay now** button. A link is for the balance when it was made: once the invoice is paid another way, changed or voided, OpenBook closes the link within a minute or two, so the customer can't pay the old amount. The next **Pay now** makes a link for what's owed then.
- An invoice's page has an **Online payment** panel: **Get a payment link for the balance** makes one to send any way you like, and **Check if paid** asks the provider about it. A PayPal link is an address on OpenBook, since PayPal's own page lasts only a few hours: it opens PayPal's page, or a new one for what's owed then.
- The **customer portal** (below) has **Pay now** on every open invoice.

The customer pays on the provider's own page; OpenBook never sees their card or PayPal sign-in. When the payment succeeds, OpenBook records an ordinary customer payment against the invoice (reference: the provider's payment ID), deposited to the account you chose. If they pay more than the balance, the rest becomes credit. A payment is never recorded twice, however many ways OpenBook hears about it.

The provider's fees aren't recorded automatically. When it pays out to your bank, make the deposit from Undeposited Funds with the payments it covers, and take the fee off it under **Fees, cash back and other money** (**Take off**, to your fees account), so the deposit is what arrived and matches the bank line.

### Refunds and disputes made at your payment provider

A refund you give in the provider's dashboard, or a dispute a customer opens, doesn't change your books by itself: the invoice still shows paid. With the webhook sending refunds and disputes (the events listed above), OpenBook notices them: the invoice's **Online payment** panel says what happened, and the home page's to-do list shows **Refunds and disputes made at your payment provider**. Then record it:

- **A refund**: open the payment, use **Change** so the refunded part isn't applied to the invoice (it becomes the customer's credit), then **Refund** that credit, paid from the account the provider takes it from. If you'd rather keep the invoice paid and give money back for something else, a credit memo and its refund do the same.
- **A dispute**: if you win it, nothing changes. If you lose it, open the payment and record **The payment came back**, with the dispute fee as the bank's fee.

Then select **Mark as dealt with**. Nothing is posted until you do one of these, so a refund is never counted twice; if you also connect Stripe as a sales channel, record the refund one way only.

> If you also connect Stripe as a sales channel, payments for invoices are recognized there and not counted as sales twice. See [Sales channels](22-sales-channels.md).

## The customer portal

A portal link is a private web address where one customer can see their account without signing in:

- their open invoices, with **Pay now** when online payments are on, and PDFs to download
- paid invoices from the last year
- estimates waiting for an answer, which they can open as a PDF and **Accept** (typing their name) or **Decline** (with a reason if they like), and those answered recently
- a statement of recent activity

On the customer's page, under **Customer portal**, select **Make a portal link**. **Copy it straight away: it's only shown once.** Send it to the customer.

Because anyone with the link can see that customer's account, share it only with them. If it might have leaked, select **Turn off** next to it and make a new one. The list shows when each link was last used.

A sub-customer billed with its parent sees the parent's account.

When a customer answers an estimate in the portal, the estimate's status changes to **Accepted** or **Declined** and its page says who answered, when, and what they wrote; the audit log records it as "portal:" and the customer's name. An expired estimate can't be accepted. If you change the status yourself afterwards, your choice stands and the customer's answer stays in the history.
