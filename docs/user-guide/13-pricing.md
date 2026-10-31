# Pricing: price levels, rules and quantity breaks

Every product or service has a **price**, its *list price*. Pricing lets the price on a sale differ from it: some customers always pay less, larger quantities cost less per unit, and promotions run for a while. You set it all up once; OpenBook then works out each line's price as you fill in a sales form.

Set it up on **Sales → Pricing**. Quantity breaks are on each product's page.

## The four tools

| Tool | For | Example |
| --- | --- | --- |
| Price level | A customer's usual pricing | Wholesale customers pay 20% less on everything |
| Quantity break | Volume pricing on one product | Widgets: 10% off from 50, 25% off from 200 |
| Price rule | Promotions and exceptions | 15% off all garden products in May |
| Rounding | Tidy prices after the changes | Round to the nearest .99 |

## Price levels

**Pricing → New price level**. Choose a type:

- **Fixed percentage on every item**: a **percent change**, such as `-10` for 10% off or `5` for 5% more.
- **Per item prices**: a price list. Enter a price for each item it covers; leave an item blank to use its own price. For long lists, **Import or export** a CSV file with *Item* and *Price* columns: export, edit in a spreadsheet, import. An import is all or nothing.

Then give customers the level on their page (**Price level**). A customer has at most one level.

## Quantity breaks

On a product's page, **Quantity breaks** lists from which quantity a lower price applies: either a **percent off** or a **unit price**. The quantity counts the item across the whole document, so 30 on one line and 30 on another reaches a break at 50. A break never raises the price.

## Price rules

**Pricing → New price rule**:

- **Name**, and **priority**: rules apply one after another, the lowest number first.
- **Price change**: **Change by percent** (`-15` for 15% off), **Change by amount** (`-2` for 2.00 off each) or **Set the price** (the unit price).
- **Items**: which products it covers (none chosen = all).
- **Applies to**: **All customers**, **Chosen customers**, or **Customers with chosen price levels**.
- **Starts / Ends**: the document dates it applies to.
- **Min quantity / Max quantity**: the item's total on the document.
- **Rounding**: the company default, or this rule's own.
- **Exclusive: don't combine with other rules**.

Leave a limit blank for no limit. Make a rule inactive when the promotion is over; it stays on record.

## How a price is worked out

For each line with **automatic price**:

1. Start with the item's list price.
2. Apply the customer's **price level**.
3. Apply the item's **quantity break**, if it lowers the price.
4. Apply the matching **price rules**, lowest priority number first, each on the result of the last. If any **exclusive** rule matches, only the exclusive rules count, and whichever gives the lowest price wins.
5. Never go below zero.
6. **Round**, with the deciding rule's rounding or else the company's, only when something changed the price. List prices are never rounded.

The company rounding is set at the bottom of the Pricing page (accountant role): no rounding, nearest cent, 5 cents, 10 cents, dollar or .99.

## On sales forms

As you choose a customer, items and quantities, each line's rate updates. Next to it you see the list price, what changed it ("Wholesale · Spring sale"), and the last price you charged this customer for the item.

If you type a different rate, it's kept as you typed it and the line stops repricing. **Automatic price** on the line switches it back.

Each line keeps its list price and where its price came from, so invoices, estimates and receipts can show **You saved** on screen and on the PDF. Turning an estimate or sales order into an invoice keeps the prices it was given; it doesn't reprice.

## Reports

- **Price list**: every item's price for one customer or one price level, as of a date. Handy to send to a customer.
- **Discounts given**: how much was taken off list prices, by pricing source or line by line.
