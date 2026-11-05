# Inventory

If you sell goods you keep in stock, make them **inventory** items. OpenBook then tracks how many you have, where, and what they cost, and records the cost of each sale as you make it. Everything here starts from **Inventory** in the menu.

## How inventory works in the books

- **Buying** stock on a bill adds it to an asset account ("Inventory Asset"): it's something you own, not an expense yet.
- **Selling** it moves its cost from the asset account to "Cost of Goods Sold", in the same moment as the sale. So the profit and loss shows each sale's income and its cost together, and the gross margin is right every month.
- **Cost** is worked out first in, first out (FIFO) by default: a sale uses up the oldest stock first, at what that stock cost. You can choose **weighted average** cost per item instead.
- Stock doesn't go below zero unless you allow it. If a sale needs more than you have, OpenBook says so; enter the bill or an adjustment for the missing stock first, or allow selling more than is on hand (below).

## Inventory items

**New item** with type **Inventory**:

- **Price** and **income account**, as for any item.
- **Inventory asset account** and **cost of goods sold account**: leave them on *Default* to use "Inventory Asset" and "Cost of Goods Sold" (created when first needed).
- **Purchase cost**: the default unit cost for stock added by adjustments.
- **Reorder point**: at or below this quantity the item is flagged for reordering.
- **Reorder quantity** and **Preferred vendor**: what [reordering stock](15-purchase-orders.md#reordering-stock) suggests, and whom it orders from. Leave the quantity blank to order back up to the reorder point.
- **Costing**: **FIFO (first in, first out)**, or **Weighted average**, which costs every sale at the average of what's on hand.
- **Lot or serial numbers**: none, lots, or serial numbers (below).
- **Barcode**: scanning it into any item picker chooses the item.
- **Selling more than is on hand**: follow the company's setting, or always or never allow it for this item (below).

Once stock has moved, the type, asset account, costing and lot/serial setting are fixed, so the history stays consistent.

## Selling more than is on hand

If you don't keep exact counts, tick **Settings → Stock → Allow selling more than is on hand**. Each item's **Selling more than is on hand** setting follows the company or makes an exception: **Always allowed** or **Never allowed**. Items with lot or serial numbers are never sold beyond what's on hand, because each unit sold needs its number. Neither are stock transfers and quantity adjustments.

When a sale (or an assembly build) needs more than you have:

- The sale goes through, and **on hand** goes below zero, for example −3.
- The missing units are costed at the item's **purchase cost** (or, without one, what its latest stock cost, else its average), so the sale still shows its cost of goods sold.
- The next stock that comes in (a bill, a return on a credit memo, a quantity adjustment or a build) fills the shortfall first. If the real cost differs from the estimate, the difference goes to cost of goods sold on that document's date, and the line says "Sold before it was in stock". On cash basis these corrections count as posted, like the cost lines of sales.

So on hand, the inventory value and the inventory asset account always agree, and cost of goods sold ends up at what the stock really cost.

A sale whose shortfall has since been filled can't be voided directly, because the stock that came in afterwards is tied to it. Void that bill, return or adjustment first, or enter a credit memo for the return instead. Voiding the bill reopens the shortfall.

## Getting stock in

- **On a bill**: add an *item line* with the item and quantity; the line's amount is the cost. See [Vendors and purchases](05-purchases.md#bills).
- **Received on a purchase order**, if your company receives goods into stock before the bill arrives (see [Purchase orders](15-purchase-orders.md)).
- **Starting stock**: when you begin, use **Adjust quantities** with the adjustment account "Opening Balance Equity" (it's offered, and added for you, if you don't have it yet) and each item's unit cost.

## Counting and adjusting

**Inventory → Adjust quantities** after a stock count, breakage or theft:

1. Set the **date** and the **adjustment account** ("Inventory Shrinkage" by default).
2. For each item, the page shows what's **on hand**. Enter the **quantity change**: positive for more, negative for less.
3. For an increase, enter the **unit cost**. A decrease is taken at its FIFO or average cost.
4. **Save adjustment**.

The value of the change goes to the adjustment account. An adjustment can be voided, unless stock it added has since been sold.

### Counting stock

For a full stock count, **Inventory → Count sheet** prints the **Physical inventory worksheet**: every inventory product with what the books hold in a warehouse and blank **Counted** and **Difference** columns to write in (lot and serial items list each lot).

Then **Inventory → Count stock**: choose the warehouse (when there's more than one), the **count date** and the **adjustment account**, and enter what was counted for each item; leave an item blank to skip it. **Record the count** makes one inventory adjustment for the differences between the counts and the books on that date: stock found comes in at the item's purchase cost, stock missing goes out at its cost. If everything matched, nothing is posted. Items with lot or serial numbers are counted on an ordinary adjustment, lot by lot.

## Selling

Inventory items go on invoices and sales receipts like any item. When you save, OpenBook takes the stock out and posts its cost. Credit memos for returned goods put stock back, at what it last sold for at cost.

Voiding a sale puts its stock back. Voiding a bill takes its stock out again, which is refused once any of that stock has been sold, so the costs never contradict each other.

## Warehouses and bins

Everyone starts with one warehouse, "Main". To keep stock in several places, add more on **Inventory → Warehouses** (name and address), and **bins** inside a warehouse if you want shelf locations.

Once you have two warehouses, invoices, receipts, credit memos and bills get a **Warehouse** field saying where the stock comes from or goes. Sales take from any bin in the warehouse; receipts arrive outside bins.

**Transfer** moves stock between warehouses and bins. It doesn't change its cost and posts nothing. The item's page shows stock by warehouse, bin and lot.

## Lots and serial numbers

For items tracked by **lot** (batches, with expiry dates) or **serial number** (one per unit):

- **Receiving** (bills, adjustments, builds) must name them. Write lots as `A-1 x 3; B-2 x 2`, or one lot alone for the whole line; serials as `S1, S2, S3`.
- **Selling**: name the lots or serials on the line, or leave them blank to take the oldest. The ones actually used are kept on the line and printed on the invoice.
- A warehouse or bin with stock in it can't be made inactive: transfer the stock out first.
- **Expiry dates** for lots are set on the item's page. Automatic picks take the soonest-expiring lot first and skip expired ones. The Inventory page lists lots expiring soon.
- **Find a lot or serial number** on the Inventory page traces it: when it came in, where it went, and what's left.

## Bundles and assemblies

- A **bundle** (kit) is sold as one line with its own price, but takes its components out of stock: a gift basket of three products. List the components and quantities on the bundle's page.
- An **assembly** is something you make from components and then stock: a computer from parts. **Build assemblies** takes the components out at their cost and puts the assemblies in at the total.

On **Build assemblies**, choose the assembly, quantity, date and warehouse, then **Check stock**. It shows what each component needs and how many you could build. Then:

- **Build** them all;
- **Build** what you can now and leave the rest **pending**; or
- **Save all as pending**, to finish later.

Pending builds wait on the Inventory page until you finish or cancel them. They post nothing and hold no stock.

Tick **Build automatically when a sale is short** on an assembly to have a sale build what it's missing from components, as part of the sale.

## Landed costs

Freight, duty and customs charges are part of what stock cost you. **Inventory → Landed cost** adds them on:

1. Enter the **date**, the **amount** and where it **comes out of**: by default "Landed Cost Clearing", where you post the freight bill itself.
2. Choose the **bills** whose goods it belongs to.
3. **Spread by** the goods' **value** or **quantity**.
4. **Post landed cost**.

The part for stock still on hand raises its cost; the part for stock already sold goes to cost of goods sold.

## Barcode labels

**Inventory → Labels** prints sheets of Code 128 barcode labels (Letter, 3 × 10, 2⅝" × 1") for chosen items, as a PDF.

## Reports

- **Inventory valuation**: quantity and value on hand as of a date, per item, or in detail by layer, bin and lot, with a line checking it against the asset accounts. Can be limited to one warehouse.
- **Physical inventory worksheet**: the count sheet (above).
- **Inventory stock status**: on hand, on purchase orders and on sales orders, what's available, and what to reorder, with a suggested quantity.
