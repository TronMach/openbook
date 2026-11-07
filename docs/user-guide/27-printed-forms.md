# Printed forms: designs, fonts and printing

Invoices, estimates, credit memos, sales receipts, statements and purchase orders print (and email) as PDFs. How they look is a **design**; you can keep several, for example a plain one for most customers and one with a different logo for a second brand.

## Designs

**Settings → Invoice and form designs** (`/settings/forms`) lists the designs with what each is used for, its template, font, paper, how many columns it prints and whether it has a logo. It needs the accountant role.

Setup makes one design, "Standard", which is the **default**: everything prints with it unless another design is chosen. To add one, fill in **New design**: a **Name**, and **Start from** either **A blank design**, a copy of one of your designs, or one of the ready-made designs, then **Create**.

### Ready-made designs

The **Ready-made designs** card has designs to start from:

- **Service invoice**: hours and rates under a colored band, no shipping address.
- **Product invoice**: item, description and SKU in a boxed grid, bill-to and ship-to side by side.
- **Professional**: a centered serif heading and one amount per line (estimates are titled "Proposal").
- **Bold**: an accent bar, a large title and a colored total, with lines to sign on estimates.
- **Minimal**: black and white with hairlines and wide margins.
- **Detailed**: small type and narrow margins for long invoices, with line numbers, item, lot and class.
- **Wide**: landscape pages with room for many columns.
- **Packing slip**: what was shipped, without prices or totals (see **Print with…** below).
- **Ink saver**: black text, thin rules and no colored bands, fills or shading, in EB Garamond (a light font that uses less ink or toner), with your company's name in Lato. Leave the logo off to save more.
- **Ink saver, compact**: the same in small type with narrow margins, and columns only as wide as they need, so long documents take fewer pages too.

**Preview** (or **as an estimate**) shows one beside the list with your company's details. **Use this design** adds it as a design of your own, which you can change like any other; nothing else changes until you make it the default or choose it for documents or customers. Adding the same one twice numbers the name ("Bold 2").

Each design has, on its page, in sections you can fold away (**Look**, **Fonts**, **Columns**, **What's shown**, **Footer**, **Email look**, **Titles and messages**); **Save design** stays at the bottom of the window:

- **Template**: **Classic (accent rule)**, **Modern (colored band)**, **Compact (smaller type)**, **Bold (accent bar, filled rows)**, **Minimal (hairlines)**, **Boxed (boxes and grid)** or **Elegant (centered, double rules)**, and an **Accent color**.
- **Font**: the built-in OpenBook Sans or any font added under **Fonts** (below). **Your company's name and details** can have a font of their own (your name, address, phone and email at the top), or **The same font**.
- **Paper**: **US Letter** or **A4**; **Margins** (Normal, **Narrow (more room for lines)**, Wide); **Text size** (Small, Medium, Large); and **Landscape pages** for many columns.
- **Logo**: upload a PNG or JPEG of up to 1 MB, choose where it goes (**Above the company name**, **Above the title, on the right** or **Centered at the top**) and the **Logo size** (Small, Medium, Large). **Remove the logo** takes it off. The Elegant template always centers it.
- **Columns**: the line table, one row per column you can print. Tick **Print** for each column you want, number them in **Order** from left to right, type a **Heading** of your own (blank keeps the usual word, for example "Hours" instead of "Qty") and set its **Width**: widths are shares of the page, so a column of 20 is twice as wide as one of 10. Tick **Fit** instead and the column is just as wide as its heading and longest entry (good for Qty, Rate and Amount); the columns with a width share what's left. **Align** lines a column's heading and entries up **Left**, **Center** or **Right** (numbers are on the right unless you change it). Up to 10 columns. Besides **Description**, **Qty**, **Rate** and **Amount** there are **#** (line numbers), **Item** (the product or service's name), **SKU** (the item's barcode), **Lot / serial**, **Class** and **Tax** (whether each line is taxed). Every item custom field is listed too, so you can print pack size, color or warranty as a column. Columns marked "when used" are left out of a document where every line is blank, so one design suits documents with and without them. Lot and serial numbers follow the description unless the design has a **Lot / serial** column.
- **Add a column after the description for item custom fields set to print as a column**: item fields you haven't placed in the table still get a column of their own when the field says to print as a column; untick it and they print under the description instead.
- **What's shown**: your company's address, phone number and email address; the customer's (or vendor's) email and phone; the customer's shipping address beside the bill-to address; due date and terms; **Totals** (untick for a packing slip); the "You saved" row; "Accepted by" and date lines to sign on estimates; shading every other line; and page numbers. Your own fields on customers and documents (PO number, ship via, sales rep…) print under the title when they're set to print on forms (**Settings → Custom fields**).
- **Footer**: printed at the bottom of every page, for example bank details or "Make checks payable to…".
- **Titles and messages**: a title and a message per kind of document, for example "Tax invoice" with a thank-you note. Blank keeps the usual title and adds no message.

The preview beside the form updates as you change things, so you can see the result before **Save design**; it stays in view as you scroll. Choose the document to preview (invoice, estimate, statement…), **Page width** or **Whole page** to fit it, **Larger preview** to give it more of the screen (remembered on this computer) and **Full screen** to fill the window (Esc closes it). If a setting can't be used (a heading that's too long, say), the reason shows above the preview, which keeps the last page it could show.

### Which design a document uses

1. The customer's own design, if one is chosen on the customer's edit page (**Form design** card).
2. Otherwise the design set for that kind of document: under **Where it's used** on a design's page, tick **Use it for every:** invoice, estimate, and so on. A kind of document belongs to one design at a time, so ticking it here takes it from the other.
3. Otherwise the default design (**Make it the default** on any design's page).

A design you no longer want can be switched off (**Switch it off**); customers that used it print with the design set for that document, or else the default. The default design can't be switched off. Designs are never deleted.

### Print with another design

When you have more than one design, invoices, estimates, credit memos, sales receipts and purchase orders have **Print with…** beside **Print**: pick a design and that one copy prints with it, for example a packing slip to go in the box with an invoice. The document's usual design doesn't change.

## Fonts

The **Fonts** card on the designs page lists the fonts you can pick in a design. Twelve come with OpenBook: Inter, Roboto, Open Sans, Lato, Montserrat, Source Sans 3, Merriweather, Lora, Source Serif 4, EB Garamond, Playfair Display and IBM Plex Mono. To add more:

- **Add from Google Fonts**: type the family's name as fonts.google.com shows it and **Add**. The server downloads its regular and bold weights once.
- **Upload your own**: a **Regular (.ttf)** file and optionally **Bold (.ttf, optional)**, then **Upload**. Only TrueType files are taken (not WOFF or OpenType CFF). Check that the font's license lets you embed it in PDFs.

A font you added can be removed (**Remove**) while no design uses it.

A form prints in its one font: a letter the font doesn't have comes out as a box or a blank, and Arabic or Hebrew text comes out left to right, reversed. The fonts that come with OpenBook cover the Latin alphabets. When a document has text beyond them, such as a customer named in Japanese or Arabic, its **Print** button shows ⚠ with **Check the PDF: some text may not print as written**. Look at the PDF before sending it; for letters, add a font that has them (Google Fonts has families such as Noto Sans JP). Right-to-left text can't be printed correctly yet.

## Printing

Every printable document has **Print**, which opens the PDF straight in the browser's print dialog, and **Print later**, which puts it in the print queue (the button then reads **In print queue ✓**; press it again to take it out).

**Sales → Print** (also on the Purchases page, `/print`) prints many documents as one PDF, each on its own pages with its own design:

- **Waiting to be printed** lists the queue. Tick what to print and **Print the ticked documents**; they're marked printed. **Take out** removes one without printing.
- **Choose documents** prints by kind and dates instead: pick the kind, **from** and **to**, **Show**, tick and print.
- **Statements**: tick customers, set **Activity from** and **to**, and **Print statements**, or **Email statements** to send each customer theirs with the usual message (email must be set up in **Settings → Email**). Customers without an email address are marked in the list and skipped; the page says how many were sent.
- **Printed recently** lists what came out in the last 30 days; **Put back in the queue** if a batch didn't come out right.
