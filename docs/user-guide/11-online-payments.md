# Getting paid online and the customer portal

Customers can pay invoices by card through Stripe, and see their account through a private portal link. Both are optional.

## Setting up online payments

You need a [Stripe](https://stripe.com) account. An administrator then fills in **Settings → Online card payments**. The page's **Setting it up, step by step** list walks through the same steps and ticks off each one that's done:

- **Take payments online**: the switch.
- **Stripe secret key**: from Stripe's dashboard (Developers → API keys). It's stored encrypted and never shown again; enter a new one to replace it.
- **Deposit payments to**: where card payments are recorded, usually **Undeposited Funds**, so you can group them into one deposit matching Stripe's payout to your bank (see [Making deposits](04-sales.md#making-deposits)).
- **Public address of OpenBook**: the address customers reach OpenBook at, for example `https://books.example.com`. Customers return here after paying, and portal links start with it.
- **Webhook signing secret** (recommended): in Stripe's dashboard, add a webhook endpoint at your public address followed by `/hooks/stripe`, for the events `checkout.session.completed`, `checkout.session.async_payment_succeeded` and `checkout.session.expired` (and `charge.refunded` and `charge.dispute.created`, below), and paste its signing secret here. Stripe then tells OpenBook the moment a payment succeeds.

If OpenBook isn't reachable from the internet, you can skip the webhook. Payments are then recorded when the customer comes back to the thank-you page, or when you select **Check if paid** on the invoice.

## How customers pay

Once online payments are on:

- **Emailed invoices** include a **Pay now** button. A link is for the balance when it was made: once the invoice is paid another way, changed or voided, OpenBook closes the link within a minute or two, so the customer can't pay the old amount. The next **Pay now** makes a link for what's owed then.
- An invoice's page has an **Online payment** panel: **Get a payment link for the balance** makes one to send any way you like, and **Check if paid** asks Stripe about it.
- The **customer portal** (below) has **Pay now** on every open invoice.

The customer pays on Stripe's own checkout page; OpenBook never sees their card. When the payment succeeds, OpenBook records an ordinary customer payment against the invoice (reference: Stripe's payment ID), deposited to the account you chose. If they pay more than the balance, the rest becomes credit. A payment is never recorded twice, however many ways OpenBook hears about it.

Stripe's fees aren't recorded automatically. When Stripe pays out to your bank, make the deposit from Undeposited Funds with the payments it covers, and take Stripe's fee off it under **Fees, cash back and other money** (**Take off**, to your fees account), so the deposit is what arrived and matches the bank line.

### Refunds and disputes made at Stripe

A refund you give in Stripe's dashboard, or a dispute a customer opens with their card company, doesn't change your books by itself: the invoice still shows paid. With the webhook sending `charge.refunded` and `charge.dispute.created`, OpenBook notices them: the invoice's **Online payment** panel says what happened, and the home page's to-do list shows **Refunds and disputes made at Stripe**. Then record it:

- **A refund**: open the payment, use **Change** so the refunded part isn't applied to the invoice (it becomes the customer's credit), then **Refund** that credit, paid from the account Stripe takes it from. If you'd rather keep the invoice paid and give money back for something else, a credit memo and its refund do the same.
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
