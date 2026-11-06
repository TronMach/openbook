# Email: writing, previewing and sending

Invoices, estimates, credit memos, sales receipts, statements, purchase orders and donor letters can all be emailed from their pages. You write the message beside a live preview of what the customer will see, in the look of the document's design, and every email sent is kept on the record it was about.

Before the first email, an administrator sets up **Settings → Email** (see [Sales](04-sales.md#sending-it)).

## Writing an email

Open the document and select **Email**. The page has the message on the left and the **preview** on the right:

- **To**: filled in from the customer's (or vendor's, or donor's) email address. Several addresses are separated by commas. **Add Cc or Bcc** shows two more boxes; addresses the customer is set to have copied are already in **Cc**.
- **Subject** and **Message**: filled in from the wording for that kind of email (below). Change them as you like; the changes are for this email only.
- **Design**: shown when you have more than one design. It changes the email's look and the PDF together, for this email only.
- **Send the invoice**: for customers, **With the PDF attached**, **As a link to view it, nothing attached**, or **Both**. It starts at what the customer is set to get.
- **When** (invoices only): **Send now** or **Send later, on** a date and time in the company's time zone.

The preview shows the **From**, **To**, **Cc** and **Subject** lines, the attachment's name, and the message as it will arrive. It changes as you type. **Attachment** (or the attachment's name) shows the PDF that goes with it, in the design chosen. Things to fix before sending (an address that isn't one, a link that can't be made) are listed under the preview.

**Send a test to me** sends exactly that email to your own address (the one on your user, else the address email is sent from), with "[Test]" before the subject. Nothing is marked as sent and its buttons don't work.

**Send** (or **Send or schedule** on invoices) sends it.

### Formatting

The buttons above the message add simple formatting, which is also what you would type:

- **B**: `**bold**`, **I**: `_italics_` (Ctrl+B and Ctrl+I in the message box).
- **Link**: `[text](https://example.com)` (Ctrl+K).
- **• List** and **1. List**: lines starting with `- ` or `1. `.

One line break in the message is one line break in the email. Only `https://`, `http://` and `mailto:` links are kept; pictures from other sites aren't loaded.

### Buttons in the email

- **Pay now**: on open invoices when online card payments are on ([Online payments](11-online-payments.md)).
- **View invoice** (estimate, credit memo, receipt): when the document goes as a link. The link opens the document as it is at that moment, without signing in.

Every email also has a plain-text copy for mail programs that don't show formatting, with the buttons written out as addresses.

## How emails look

Each design has an **Email look** (**Settings → Invoice and form designs**, open a design, **Email look**):

- **With the logo, over a band of the accent color** (the usual).
- **Simple**: the company name in the accent color, no logo.
- **Plain text only**.

The design's **Footer** closes every email, under your company's name, address, phone and email. An email uses the same design as its PDF: the customer's own design, else the design for that kind of document, else the default (see [Printed forms](27-printed-forms.md)).

## The wording of each email

**Settings → Email messages** lists every kind of email (invoice, estimate, credit memo, sales receipt, statement, purchase order, gift acknowledgment, giving statement, scheduled report, invitation, payment receipt) with its subject and whether it's still the usual wording. It needs the accountant role.

**Change** opens the wording beside a preview with sample values. Words in braces are filled in when an email is written: click one under **Fill in** to add it where the cursor is (hover over it to see what it means). For example an invoice can use `{customer}`, `{number}`, `{date}`, `{due}`, `{total}`, `{balance}`, `{terms}` and `{company}`. A word in braces the email doesn't know (a typing slip like `{amout}`) is pointed out under the preview and can't be saved.

A line whose fill-ins all come out blank is left out: the estimate's "It's valid until {valid_until}." disappears for an estimate without an end date.

**Preview in the look of** shows the wording in another design's look. **Save**, or **Go back to the usual wording**.

### Payment receipts

A customer payment's page has **Email receipt**: a thank-you that says how much was received, how it was paid (`{paid_by}`, "by check no. 1042"), a line per invoice it paid with what's still owed on each (`{applied}`), any amount kept as credit (`{credit}`) and the customer's balance afterwards. Nothing is attached: the message says it all. It's sent and kept in the payment's email history like any other.

Payment reminders are worded on **Sales → Collections → Settings** ([Collections](10-collections.md)), with the same formatting buttons, fill-ins and preview.

## Customers' email preferences

On a customer's page, **Email**:

- **Copy every email to**: up to five addresses (their accounts payable inbox, say), filled into **Cc** on every email to them, reminders and statements included.
- **Send their invoices, estimates, credit memos and receipts**: **With the PDF attached**, **As a link to view it**, or **Both**.

Links need the address customers open OpenBook at: an administrator enters it in **Settings → Email** (**Address customers open OpenBook at**, for example `https://books.example.com`; it must be reachable from the internet). Until then, emails to customers set to links carry the PDF instead, and the email page says so. On the desktop install OpenBook is only on your computer, so links can't be opened by customers.

## What was sent

Each document's page (and the customer's page, for statements) lists **Emails sent**: the subject, who it went to and when, and by whom. Open one to see its copies, what was attached and the message as sent. For an email sent as a link it shows whether it has been opened (first and last time), and **Turn the link off** stops that link working at once; sending the document again makes a new one.

Every send is also in the audit log (**Emailed**), and a changed wording is recorded there as well.

## Sending to many at once

**Email statements** on the Print page ([Printed forms](27-printed-forms.md)) and **email every donor with an address** for giving statements ([Nonprofits](23-nonprofits.md)) send each one the usual wording for that kind of email, with their copies, in their own design (giving statements in the default design). Those without an email address are listed, not sent.
