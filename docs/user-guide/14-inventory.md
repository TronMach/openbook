# Inventory

If you sell goods you keep in stock, make them **inventory** items. OpenBook then tracks how many you have, where, and what they cost, and records the cost of each sale as you make it. Everything here starts from **Inventory** in the menu.

## How inventory works in the books

- **Buying** stock on a bill adds it to an asset account ("Inventory Asset"): it's something you own, not an expense yet.
- **Selling** it moves its cost from the asset account to "Cost of Goods Sold", in the same moment as the sale. So the profit and loss shows each sale's income and its cost together, and the gross margin is right every month.
- **Cost** is worked out first in, first out (FIFO) by default: a sale uses up the oldest stock first, at what that stock cost. You can choose **weighted average** cost per item instead.
- Stock can't go below zero. If a sale needs more than you have, OpenBook says so; enter the bill or an adjustment for the missing stock first.

## Inventory items

**New item** with type **Inventory**:

- **Price** and **income account**, as for any item.
- **Inventory asset account** and **cost of goods sold account**: leave them on *Default* to use "Inventory Asset" and "Cost of Goods Sold" (created when first needed).
- **Purchase cost**: the default unit cost for stock added by adjustments.
- **Reorder point**: at or below this quantity the item is flagged for reordering.
- **Costing**: **FIFO (first in, first out)**, or **Weighted average**, which costs every sale at the average of what's on hand.
- **Lot or serial numbers**: none, lots, or serial numbers (below).
- **Barcode**: scanning it into any item picker chooses the item.

Once stock has moved, the type, asset account, costing and lot/serial setting are fixed, so the history stays consistent.

## Getting stock in

- **On a bill**: add an *item line* with the item and quantity; the line's amount is the cost. See [Vendors and purchases](05-purchases.md#bills).
- **Received on a purchase order**, if your company receives goods into stock before the bill arrives (see [Purchase orders](15-purchase-orders.md)).
- **Starting stock**: when you begin, use **Adjust quantities** with the adjustment account "Opening Balance Equity" and each item's unit cost.

## Counting and adjusting

**Inventory → Adjust quantities** after a stock count, breakage or theft:

1. Set the **date** and the **adjustment account** ("Inventory Shrinkage" by default).
2. For each item, the page shows what's **on hand**. Enter the **quantity change**: positive for more, negative for less.
3. For an increase, enter the **unit cost**. A decrease is taken at its FIFO or average cost.
4. **Save adjustment**.

The value of the change goes to the adjustment account. An adjustment can be voided, unless stock it added has since been sold.

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
- **Inventory stock status**: on hand against reorder points: what to reorder.
