# Printed forms: designs, fonts and printing

Invoices, estimates, credit memos, sales receipts, statements and purchase orders print (and email) as PDFs. How they look is a **design**; you can keep several, for example a plain one for most customers and one with a different logo for a second brand.

## Designs

**Settings → Invoice and form designs** (`/settings/forms`) lists the designs with what each is used for, its template, font, paper and whether it has a logo. It needs the accountant role.

Setup makes one design, "Standard", which is the **default**: everything prints with it unless another design is chosen. To add one, fill in **New design**: a **Name**, and **Start from** either **A blank design** or a copy of an existing one, then **Create**.

Each design has, on its page:

- **Template**: **Classic**, **Modern (colored band)** or **Compact (smaller type)**, and an **Accent color**.
- **Font**: the built-in OpenBook Sans or any font added under **Fonts** (below).
- **Paper**: **US Letter** or **A4**.
- **Logo**: upload a PNG or JPEG of up to 1 MB, choose where it goes (**Above the company name**, **Above the title, on the right** or **Centered at the top**) and the **Logo size** (Small, Medium, Large). **Remove the logo** takes it off.
- **What's shown**: **The customer's shipping address, beside the bill-to address**; **Due date and terms**; **Quantity and rate columns** (off: only the amount).
- **Column headings**: your own words for Description, Quantity, Rate and Amount (blank keeps the usual word).
- **Footer**: printed at the bottom of every page, for example bank details or "Make checks payable to…".
- **Titles and messages**: a title and a message per kind of document, for example "Tax invoice" with a thank-you note. Blank keeps the usual title and adds no message.

The preview beside the form updates as you change things, so you can see the result before **Save design**.

### Which design a document uses

1. The customer's own design, if one is chosen on the customer's edit page (**Form design** card).
2. Otherwise the design set for that kind of document: under **Where it's used** on a design's page, tick **Use it for every:** invoice, estimate, and so on. A kind of document belongs to one design at a time, so ticking it here takes it from the other.
3. Otherwise the default design (**Make it the default** on any design's page).

A design you no longer want can be switched off (**Switch it off**); documents and customers that used it print with the default. The default design can't be switched off. Designs are never deleted.

## Fonts

The **Fonts** card on the designs page lists the fonts you can pick in a design. Twelve come with OpenBook: Inter, Roboto, Open Sans, Lato, Montserrat, Source Sans 3, Merriweather, Lora, Source Serif 4, EB Garamond, Playfair Display and IBM Plex Mono. To add more:

- **Add from Google Fonts**: type the family's name as fonts.google.com shows it and **Add**. The server downloads its regular and bold weights once.
- **Upload your own**: a **Regular (.ttf)** file and optionally **Bold (.ttf, optional)**, then **Upload**. Only TrueType files are taken (not WOFF or OpenType CFF). Check that the font's license lets you embed it in PDFs.

A font you added can be removed (**Remove**) while no design uses it.

## Printing

Every printable document has **Print**, which opens the PDF straight in the browser's print dialog, and **Print later**, which puts it in the print queue (the button then reads **In print queue ✓**; press it again to take it out).

**Sales → Print** (also on the Purchases page, `/print`) prints many documents as one PDF, each on its own pages with its own design:

- **Waiting to be printed** lists the queue. Tick what to print and **Print the ticked documents**; they're marked printed. **Take out** removes one without printing.
- **Choose documents** prints by kind and dates instead: pick the kind, **from** and **to**, **Show**, tick and print.
- **Statements**: tick customers, set **Activity from** and **to**, and **Print statements**.
- **Printed recently** lists what came out in the last 30 days; **Put back in the queue** if a batch didn't come out right.
