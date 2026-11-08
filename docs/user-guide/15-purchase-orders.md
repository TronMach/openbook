# Purchase orders, approvals and ACH payments

[Vendors and purchases](05-purchases.md) covers bills and paying them. This chapter adds controls for businesses that order ahead or want a second pair of eyes before money goes out.

## Purchase orders

A *purchase order* (PO) is what you send a vendor to order goods or services. It posts nothing by itself. Later you record what arrives and the vendor's bill against it, and OpenBook checks one against the others.

**Purchases → Purchase orders → New**:

- **Vendor**, **order number** (its own sequence), **date** and **expected** delivery date.
- Lines: an **inventory item** with quantity and unit cost, or a **category** line (an account and an amount) for anything else.
- **Message to the vendor**, printed on the PO.

**Save purchase order**. From its page, **PDF** prints it, and **Email** opens a message to the vendor beside a live preview ([Email](29-email.md)). A PO can be edited until something is received or billed against it. It keeps the vendor's address as it was when the order was made (or its vendor changed), so a reprint shows where it went even after the vendor moves.

### Receiving goods

When a delivery arrives, use **Receive goods** on the PO: enter what arrived on each line, the **date** and a **note** (a packing slip number, say), then **Record receipt**. A delivery recorded by mistake can be taken back with **Take back**.

By default, receiving only records what arrived, to check the bill against; the stock enters the books when the bill is entered. If you want stock available to sell as soon as it arrives, an accountant can turn on receiving into stock in the approval settings (below). Received goods are then posted at the order's cost against "Received Not Billed", and the bill clears that account later, with any price difference going to "Purchase Price Variance".

With receiving into stock on, a delivery doesn't count units a bill has already brought in, so goods billed before they arrive go into stock once. The delivery still records everything that arrived. Because of that, a bill can't be voided or cut back while a delivery depends on its units, and a delivery can't be taken back while a later one depends on it: take the later delivery back first. Goods can still be received on a closed order, which closes once everything is billed, even if some haven't arrived.

### Billing from the PO

When the vendor's bill comes, use **Enter the vendor's bill** on the PO. For items tracked by lot or serial number, enter what arrived in **Lots or serial numbers**. Enter **their invoice number** and the **bill date**. By default the bill covers what's been received and not yet billed (or everything left, if nothing was received); change the quantities and costs to match the vendor's bill. **Enter bill**.

The PO tracks what's been ordered, received and billed, and closes when everything is billed. **Cancel the rest of the order** if the vendor can't supply it. Voiding a bill puts its quantities back on the PO.

Goods that have arrived have to be billed, or their delivery taken back, before the rest of an order can be cancelled: a cancelled order takes no bills.

To correct a bill entered from a PO, use **Change** on the bill. It lists the order's lines with what's been ordered, received and billed on other bills: change the quantity and unit cost (or the amount, on a category line) the bill covers, and the **Other lines** (freight, tax) that aren't on the order. Say **Why are you changing it?** and **Save changes**. OpenBook bills the order again as if the bill were entered now: goods received into stock clear "Received Not Billed" again, the 3-way match and approval rules run again, and the PO closes or reopens with what's left. Changing it is refused once stock it brought in has been sold or used, and on a cancelled PO (void the bill instead).

### Reordering stock

**Purchases → Reorder stock** (also on the Inventory page) lists the inventory products at or below their reorder point once open orders are counted: what's on hand, plus what's on purchase orders and not in stock yet, less what open sales orders still have to deliver. Each line suggests the item's **reorder quantity**, or enough to bring it back above the reorder point, and the item's **preferred vendor** and purchase cost (both set on the item's page).

Each line also shows what the item **sold a month** over the last 90 days and how many days what's available **lasts** at that rate. Give an item a **lead time in days** (how long a new order takes to arrive) and it's listed as soon as what's left wouldn't last that long, whatever its reorder point; with no reorder quantity set, it suggests enough for the lead time and a month more.

Tick the items to order, change vendors, quantities or costs if you like, choose the **order date** (and an **expected by** date), then **Make purchase orders**. OpenBook makes one purchase order per vendor, with the memo "Reorder"; if any of them can't be made, none are. **Every inventory product** lists items that don't need ordering too, so you can add them. Assemblies aren't listed: you build them.

## The 3-way match and approvals

When a bill comes from a PO, OpenBook compares the order, what was received and the bill. It holds the bill for approval if:

- it bills more than was ordered;
- it bills more than has been received (when that check is on);
- a unit cost is higher than the order's by more than the **price tolerance**.

It also holds any bill at or above the **approval limit**, PO or not.

An accountant sets these on **Purchases → Approvals → Settings**:

- **Approval limit**: bills of this total or more wait for approval. Blank for no limit.
- **Price tolerance (%)**: how far a cost may be over the PO before it's held.
- **Hold bills from purchase orders that bill more than has been received**.
- **Let the person who entered a bill approve it too**, for one-person offices. Otherwise someone else has to.
- Receiving into stock (above).

Held bills appear on **Purchases → Approvals** with the reasons. An accountant reviews each one, adds a **note** if needed, and selects **Approve** or **Reject** (a note is required to reject). Held and rejected bills can't be paid: **Pay bills** shows them unticked. A rejected bill can still be approved later, or voided.

Every bill records who entered it, and approved bills record who approved them and when.

## ACH payments

Instead of printing checks, you can pay vendors by direct deposit using an ACH file you upload to your bank's business banking site. This needs ACH origination set up with your bank.

1. **The bank's details** (once per bank account): on **Purchases → ACH payments**, choose the bank account and fill in **Your bank's ACH details**: routing number, bank name, immediate origin, origin name, company ID, company name and entry description. Your bank gives you these.
2. **Each vendor's details**: on the vendor's page, under **Bank details for ACH payments**, enter their routing number, account number and type (checking or savings). The account number is stored encrypted and only its last four digits are shown again.
3. **Pay the bills** as usual with **Pay bills**, from that bank account.
4. Back on **ACH payments**, the payments to vendors with bank details are listed under **Payments ready to send**, unticked and with their reference, so a check you've written isn't sent again; tick the ones to send. Payments already matched to a bank line or reconciled aren't listed. Choose **Pay on** (the effective date, a banking day; most banks need the file a day or two ahead), tick them, and **Make ACH file**.
5. Download the file from **Files made** and upload it to your bank.

A payment goes in one file only, and payments printed as checks are left out.

If the bank never got a file, or refused it, open **Withdraw…** beside it under **Files made**, give the reason and click **Withdraw the file**: its payments come back to **Payments ready to send** (or can be printed as checks or voided), and the file can't be downloaded again. A file can't be withdrawn once one of its payments has come through the bank (matched to a bank line or reconciled). If a payment in a file is voided after the file is made, the file is marked: don't upload it as it is. Withdraw it and make a new one.
