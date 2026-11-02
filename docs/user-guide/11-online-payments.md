# Getting paid online and the customer portal

Customers can pay invoices by card through Stripe, and see their account through a private portal link. Both are optional.

## Setting up online payments

You need a [Stripe](https://stripe.com) account. An administrator then fills in **Settings → Online card payments**. The page's **Setting it up, step by step** list walks through the same steps and ticks off each one that's done:

- **Take payments online**: the switch.
- **Stripe secret key**: from Stripe's dashboard (Developers → API keys). It's stored encrypted and never shown again; enter a new one to replace it.
- **Deposit payments to**: where card payments are recorded, usually **Undeposited Funds**, so you can group them into one deposit matching Stripe's payout to your bank (see [Making deposits](04-sales.md#making-deposits)).
- **Public address of OpenBook**: the address customers reach OpenBook at, for example `https://books.example.com`. Customers return here after paying, and portal links start with it.
- **Webhook signing secret** (recommended): in Stripe's dashboard, add a webhook endpoint at your public address followed by `/hooks/stripe`, for the events `checkout.session.completed`, `checkout.session.async_payment_succeeded` and `checkout.session.expired`, and paste its signing secret here. Stripe then tells OpenBook the moment a payment succeeds.

If OpenBook isn't reachable from the internet, you can skip the webhook. Payments are then recorded when the customer comes back to the thank-you page, or when you select **Check if paid** on the invoice.

## How customers pay

Once online payments are on:

- **Emailed invoices** include a "Pay online" link.
- An invoice's page has an **Online payment** panel: **Get a payment link for the balance** makes one to send any way you like, and **Check if paid** asks Stripe about it.
- The **customer portal** (below) has **Pay now** on every open invoice.

The customer pays on Stripe's own checkout page; OpenBook never sees their card. When the payment succeeds, OpenBook records an ordinary customer payment against the invoice (reference: Stripe's payment ID), deposited to the account you chose. If they pay more than the balance, the rest becomes credit. A payment is never recorded twice, however many ways OpenBook hears about it.

Stripe's fees aren't recorded automatically. When Stripe pays out to your bank, make the deposit from Undeposited Funds for the full amount of the payments, and record Stripe's fee as an expense from the same bank account (**Check / payment**, to your fees account) on the same date. Together they come to what arrived, so reconciling works.

> If you also connect Stripe as a sales channel, payments for invoices are recognized there and not counted as sales twice. See [Sales channels](22-sales-channels.md).

## The customer portal

A portal link is a private web address where one customer can see their account without signing in:

- their open invoices, with **Pay now** when online payments are on, and PDFs to download
- paid invoices from the last year
- a statement of recent activity

On the customer's page, under **Customer portal**, select **Make a portal link**. **Copy it straight away: it's only shown once.** Send it to the customer.

Because anyone with the link can see that customer's account, share it only with them. If it might have leaked, select **Turn off** next to it and make a new one. The list shows when each link was last used.

A sub-customer billed with its parent sees the parent's account.
