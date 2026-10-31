# Custom fields, notes and attachments

Three ways to keep more on a record than OpenBook asks for.

## Custom fields

A *custom field* adds your own detail to products and services, customers, vendors or documents: a pack size, a sales rep, a contract number, a delivery instruction. Accountants set them up on **Settings → Custom fields**.

### Ready-made fields

The page starts with a list of common fields: tick the ones you want and **Add the ticked fields** (or **Add all**). For example:

- **Products and services**: pack size, unit, brand or manufacturer, manufacturer part number, model number, size, color, material, weight, dimensions, country of origin, HS tariff code, minimum order quantity, warranty, product web page.
- **Customers**: customer type, sales rep, territory or region, referral source, customer since, delivery instructions, receiving hours, preferred carrier, contract number, preferred contact method.
- **Vendors**: vendor type, rep name, rep direct phone, minimum order amount, ship via, insurance certificate expiry, certifications, ordering or payment portal.
- **Documents**: ship via, ship date, tracking number, FOB, requested delivery date, sales rep, job or site name, contract number, delivery instructions.

Add the ones you want and change them afterwards like any other field.

### Making your own

**New field** (or **Add a field**):

- Who it's for: products and services, customers, vendors or documents.
- **Name** and **kind of value**: text, long text, whole number, number with decimals, money, date, yes / no, pick list, web link, email or phone. The kind can't change once anything has a value.
- **Order** (lower comes first), **help text** shown under the field, and whether it's **required**.
- **Pick list choices**, one per line, for pick lists.
- **Default value**: filled in on new documents, or suggested on records with no value yet.
- Limits: **longest text allowed**, **smallest** and **largest value**.
- **Show it as a column** in the list of customers, vendors or products.
- **Print it on PDFs**, and for product fields on document lines, whether it goes **under the description** ("Pack size: Case of 12") or in **its own column**.
- For document fields, **which documents** have it (invoices, estimates, sales orders, sales receipts, credit memos, purchase orders, bills). For customer and vendor fields, which documents it's **copied onto**.

A field you no longer need is **switched off**, never deleted; values already recorded are kept.

### Filling them in

Records and documents show their fields in a panel near the bottom of the page, with **Save**. Product fields also show under each line's description on documents.

When you create a document, values are copied in:

1. from the document it came from (an invoice from a sales order or estimate, a bill from a purchase order);
2. from the customer's or vendor's fields marked to be copied onto that document. If the document has a field of the same name, the value goes into it, so "Sales rep" on a customer fills "Sales rep" on the invoice and can be changed there;
3. then document field defaults.

After that, the document's values are its own: changing the customer later doesn't change old invoices.

### Using them

- **Search** finds records and documents by their custom field values.
- **CSV imports** of customers, vendors and products read a column named after a field.
- The **custom transaction report** can add customer, vendor or document field columns and filter by a field's value ([chapter 20](20-budgets-and-analysis.md#building-your-own-report)).

## Notes

Most record pages (customers, vendors, products, accounts, documents) have **Notes**: short free-text notes, each showing who wrote it and when. **Add a note** (`Ctrl` + `Enter` saves it). **Remove** takes one off; adding and removing are both recorded in the record's history.

Use notes for things like "Prefers calls after 3pm", "Disputed line 2, credit promised" or "Reorder from the other supplier when out".

## Attachments

Documents, customers, vendors and many other records have **Attachments**: the receipt, the vendor's bill, a signed contract, a tax-exemption certificate.

**Attach** a file: PDF, images, text, CSV, Word or Excel, up to 10 MB. Files are kept inside the company file, so they're in every backup. PDFs and images open in the browser; other files download. Web pages and scripts are refused for safety.

Files are copied onto the new record when one document becomes another (a document from the inbox becomes a bill, a receipt becomes an expense). Attaching and removing files is recorded in the record's history.
