# Sales channels: Shopify, Stripe, Amazon, eBay, Etsy and PayPal

If you sell through online shops and marketplaces, you don't enter each sale as an invoice. The channel collects the money, takes its fees, and pays out to your bank every few days. OpenBook records each **payout** in one entry, split into sales, shipping, tax, refunds and fees, so the deposit matches your bank and the profit and loss shows the real figures. Optionally it also takes the sold items out of stock.

In the e-commerce edition this comes first on the home page. Otherwise go to **Sales → Sales channels**.

## Adding a channel

**Add a channel**: choose the kind (Shopify, Stripe, Amazon, eBay, Etsy, PayPal, or **Other (CSV)** for anything else) and a name.

Each channel has **settings** for where things go. Leave them on the defaults unless your chart has its own accounts; the defaults are created when first needed:

| Part of a payout | Default account |
| --- | --- |
| The money that reaches you | A bank account, or Undeposited Funds |
| Sales | Online Sales (income) |
| Shipping charged | Shipping Income |
| Refunds | Refunds and Returns (income, reducing sales) |
| Fees | Merchant and Marketplace Fees (expense) |
| Sales tax | Sales Tax Payable |
| Adjustments | Marketplace Adjustments |

**The marketplace collects and pays sales tax on these orders** is on by default for Amazon, eBay and Etsy, which collect and pay sales tax themselves in most states. That tax passes through the tax account and nets to zero, and the **Sales tax by state (online orders)** report shows it separately.

## Getting payouts in

### Importing a payout report

On the channel's page, **Import a payout report**. The page says where to download the report for each channel:

- **Amazon**: Seller Central › Reports › Payments › All statements › the settlement's flat file (V2).
- **eBay**: Seller Hub › Payments › Reports › Transaction report (CSV).
- **Etsy**: Shop Manager › Finances › Payment account › the monthly CSV.
- **PayPal**: Activity › Statements › Activity download, CSV, completed payments. Withdrawals to your bank are left out.
- **Shopify**: Finances › Payouts › Export transactions (CSV), with the payout ID column.
- **Stripe**: Balance › Payouts › Export, or Reports › Payout reconciliation (CSV).
- **Other (CSV)**: any file with a date, a type (sale, refund, fee, shipping, tax…) and an amount (or gross and fee) column; a payout ID column groups rows into payouts.

Payouts already imported are skipped, so overlapping files are safe.

### Live connection (Shopify and Stripe)

Shopify and Stripe channels can fetch payouts by themselves, every six hours or with **Fetch payouts now**. Under **Live connection** (accountant role):

- **Shopify**: in Shopify, Settings › Apps and sales channels › Develop apps › Create an app, give it the Admin API scopes `read_shopify_payments_payouts` and, to bring in orders too, `read_orders`. Install it and copy the **Admin API access token**. Enter it with your **store address**.
- **Stripe**: Developers › API keys › create a **restricted key** with read access to Balance and Payouts (a secret key works too).

The key is stored encrypted. If a fetch fails, the channel's page says why. **Disconnect** stops fetching.

If you also take invoice payments through Stripe ([Getting paid online](11-online-payments.md)), those charges are recognized in the payout and cleared against the payments already recorded, so they aren't counted as sales twice. Don't deposit those invoice payments separately as well.

## Posting a payout

New payouts are **waiting to post**. Open one to see its parts by account, its net payout, and each transaction in it. If the channel reported a net amount, the parts must add up to it before you can post.

**Post payout** records one entry: the net to your deposit account, and each part to its account. To undo, **Void the entry**; the payout goes back to waiting. **Dismiss** a payout that's a duplicate or doesn't belong; **Restore** brings it back.

When the money reaches your bank, match the bank line to the posted payout in the review list.

## Orders and stock

Payouts carry the money; they don't say which products were sold. If you track inventory, bring in **orders** too, so stock and cost of goods sold stay right. Revenue still comes from the payouts.

- **Import orders**: an orders report from the channel (Amazon All Orders, eBay, Etsy, Shopify exports, or a general CSV). Cancelled orders are left out; orders already seen are skipped.
- **Shopify** live connections fetch orders with the payouts.

Each import makes an order batch. Before it can be posted, every **SKU** must be mapped to one of your items, or marked as not stocked: **Map SKUs to items** on the channel's page lists them. A SKU that equals an item's barcode or name is matched by itself.

**Post stock** takes the items out of stock at cost (no revenue). **Put the stock back** reverses it.

### Returns

On a posted batch, enter the quantities that came back under **Returns** and **Put the returns in stock**. They go back at their latest sale cost. The refund itself arrives with the channel's payout.

## Reports

- **Sales by channel**: what each channel's posted payouts came to: sales, shipping, refunds, fees and sales tax.
- **Sales tax by state (online orders)**: sales, shipping and tax on channel orders by ship-to state, with what you collected apart from what marketplaces collected.
