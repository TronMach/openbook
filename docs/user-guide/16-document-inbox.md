# The document inbox and receipt capture

Paper and PDFs arrive all day: vendors' invoices, customers' purchase orders, packing slips, quotes, till receipts. Instead of typing them in, upload the file. OpenBook reads it, fills in what it can, and lets you check it before anything is posted.

## Two places to upload

| Upload to | For | Becomes |
| --- | --- | --- |
| **Purchases → Receipts** | Till receipts and card slips for things already paid | An expense |
| **Document inbox** (from Sales or Purchases) | Vendor invoices, customer purchase orders, packing slips, vendor quotes | A bill, a sales order, a received delivery, a purchase order |

## How files are read

By default everything is read **on your own server**; nothing leaves it.

- **PDFs** with text in them (most PDFs made by accounting software) and **text files** are read directly.
- **Photos and scans** need an OCR program, Tesseract, installed on the server and configured by whoever runs it. Without it, photos are kept but not read, and you fill in the details yourself.

For the document inbox, an administrator can also switch on **reading with Claude** on **Settings → Document reading**: tick **Let documents be read by Claude**, enter an Anthropic API key (stored encrypted), and keep the suggested model unless you have a reason not to. Claude reads photos, scans and unusual layouts much better than the server's own reader. Once this is on, files are sent to Anthropic unless the uploader unticks **Read it with Claude** for that upload, and uploads through the API unless they say `claude=false`. If Claude can't read a file, OpenBook falls back to reading it on the server and says why.

However a file is read, check the result against the document before you post it.

## Receipts

**Purchases → Receipts → Upload receipts**: choose a photo or PDF, or several at once (up to 10). OpenBook keeps the file and tries to read the vendor (vendors you already have are recognized by name), the date, the total and the tax.

Open the receipt to **Record as an expense**: check the **vendor** or **payee**, choose **paid from** (the card or bank account), the **category**, **date** and **amount**, then **Record expense**. The receipt file is attached to the expense. The text that was read is shown below the form so you can compare.

**Dismiss** a receipt that isn't an expense or was entered already, for example because it also arrived through a bank import. If the same file was uploaded before, the receipt's page says so, with a link to the earlier one.

## The document inbox

Upload a file, or several of the same kind at once (up to 10), and say **what it is**: vendor invoice, customer purchase order, packing slip or vendor quote. **Upload and read**. One file opens straight away; several are listed in the inbox to check one by one, with **Next to check** on each page.

On a purchase order's page, **upload the vendor's invoice** and **upload the packing slip** open the inbox for that order: what's read is matched to the order unless the document names another one.

Open it from the inbox to see the file beside what was read:

- **Vendor** or **customer**: matched by name. If the document names someone not on your list, it says so; choose the right one.
- The **number** (invoice number, their PO number, slip number), **dates**, **subtotal**, **tax**, **shipping** and **total**.
- **Our purchase order**: on vendor documents, the PO number they quote is matched to yours, ignoring prefixes ("PO-1004" finds 1004).
- **Lines**, each with code, description, quantity, unit price and amount, and the **item** it was matched to: by barcode, name, the PO's line, or a code you taught it before.

Correct anything wrong and **Save**. If the lines don't add up to the total, OpenBook warns you to look for a missed or misread line. When you save, it remembers the item you chose for each product code from that vendor or customer, so next time it matches by itself.

Before you enter it, the page warns when the document may already be in the books: the same file uploaded before (to the inbox or as a receipt), a bill from the vendor with the same invoice number, or one for the same total within two weeks of its date. Dismiss it if it's a copy.

Documents waiting to be checked and receipts waiting to be recorded are listed on the home page's to-do list.

**Read again** (with Claude or on the server) replaces the fields, including your corrections. **Dismiss** removes a document from the inbox; **Put it back in the inbox** restores it.

## Turning a document into a transaction

The bottom of the page offers the next step for its kind:

- **Vendor invoice → Enter the bill.** With a matched PO, lines for the PO's items bill the order (with the usual 3-way match, see [Purchase orders](15-purchase-orders.md)); other lines are added as extras. Without a PO, inventory items go in as item lines and the rest as category lines; choose a **category** per line or a **default category**. Tax and shipping go to the accounts you choose in **Sales tax to** and **Shipping to**. The bill is refused if the lines, tax and shipping don't add up to the total.
- **Customer purchase order → Make the sales order**, with the customer's PO number and their prices (or your automatic prices where they didn't give one).
- **Packing slip → Receive the goods** on the matched purchase order, on the date you choose.
- **Vendor quote → Make the purchase order.**

The file is attached to the new record, and the document is marked done with a link to it.
