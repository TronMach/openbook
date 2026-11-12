# Vendors and purchases

This chapter covers what you buy: entering vendors' bills, paying them, and recording expenses you pay on the spot. Purchase orders, approvals and ACH payments are in [chapter 15](15-purchase-orders.md), and the document inbox in [chapter 16](16-document-inbox.md).

Everything here starts from **Purchases** in the menu.

## Two ways to record a purchase

| When | Use | What it does |
| --- | --- | --- |
| The vendor sends a bill you'll pay later | **Bill**, then **Pay bills** | Records what you owe (accounts payable) now; the payment later clears it |
| You pay straight away: a card purchase, a check written at the counter, a direct debit | **Expense** | Records the cost and the money leaving the bank or card together |

Use one or the other for the same purchase, never both, or it will be counted twice.

## Vendors

**Vendor Center** on the vendor list shows the list and one vendor side by side, with every bill, payment, expense, credit and purchase order they have; see [The simple view](28-simple-view.md#customer-vendor-and-item-centers).

**Purchases → Vendors → New vendor** (or `n` then `v`):

- **Display name** (unique), **company**, **email**, **phone** and **address**.
- **Your account number with them** is used as the memo on bill payments, so it prints on the check.
- **Payment terms** set the due date of their bills.
- **Default category** is the account their bills and expenses start with, for example "Rent" for your landlord.
- **1099**: tick **Track payments for a 1099** for contractors, landlords, attorneys and others you report to the IRS, choose the form and box, and enter their **tax ID** from their W-9. After saving, the tax ID is only shown masked (last four digits).

Vendors with unpaid bills can't be made inactive. Vendors can be imported from a CSV file with **Vendors → Import CSV**. Besides names and contact details it reads a **1099** (or **Eligible for 1099**) yes/no column, a **Tax ID** and a **1099 box** (NEC, or MISC 1 rents, 3 other income, 6 medical, 10 attorney); a box alone turns tracking on. The preview shows only the last four digits of a tax ID.

## Bills

**Purchases → New bill** (or `n` then `b`):

1. Choose the **vendor**. Their terms fill in the **due date**, and their default category fills in the first line.
2. Enter the vendor's **bill no.** (their invoice number; OpenBook won't let you enter the same number twice for one vendor) and the **bill date**.
3. Add a line per category: the **account** the cost belongs to (Office Supplies, Rent, Cost of Goods Sold…), a description and the amount. A negative amount is a credit on the bill.
4. For stock you buy, add **item lines** instead: the inventory item and quantity. They add to stock on hand at the line's cost.
5. **Save bill**.

If the vendor has a bill for the same amount within two weeks of the date, OpenBook shows it before saving, in case it's the same bill entered twice. Open it to check; **Save anyway** if it's a different one.

Attach the vendor's PDF or a photo to the bill under **Attachments** on its page, so it's there when you need it.

To correct a bill, use **Change** at the top of its page: the bill number, dates, lines, location, warehouse and memo. Say **Why are you changing it?** and **Save changes**. OpenBook reverses the bill's journal entry on its original date and posts the corrected one, so the bill keeps its payments and history; each change is listed under **Changes** on the bill.

- The vendor can't change: to bill a different vendor, void the bill and enter a new one.
- If part of the bill is paid or credited, the new total can't be less than that. Changing what a bill paid in a closed period was for needs the closing date password, like any change dated in a closed period.
- Stock the bill brought in is received again, so the change is refused once some of it has been sold, used, moved or given landed costs.
- Lines marked billable to a customer stay as they are, on the same line; remove the mark first to change one.
- If the bill is over the approval limit after the change, it waits for approval again, even if it was approved before, and the person who changed it can't approve it (unless self-approval is on).
- A bill entered from a purchase order is changed by order line instead (see [Purchase orders](15-purchase-orders.md)).

**Copy** at the top of a bill starts a new bill for the same vendor with the same lines, dated today (enter the vendor's new bill number). Purchase orders have **Copy** too; a bill entered from a purchase order doesn't (enter the next one from the order).

A bill can be voided (with a reason) from its page until a payment is made against it.

Bills that arrive regularly with the same amounts, such as rent, can be entered automatically: see **Purchases → Recurring bills**.

## Paying bills

**Purchases → Pay bills** lists every unpaid bill with its due date; overdue ones are highlighted.

1. Choose the account to **pay from**: a bank account (checks, transfers) or a credit card.
2. Set the **payment date**.
3. For a bank account, enter the **first check number**. OpenBook writes one payment per vendor and numbers the checks upward from it. For a card, enter a reference.
4. Tick the bills to pay. Change the amount on a row to pay part of a bill. Use **Show bills from** to narrow the list to one vendor.
5. Select **Pay** (it shows the total).

To print checks, go to **Banking → Print checks**. Bills on hold for approval are listed unticked and can't be paid until approved.

To correct a bill payment, open it and use **Change**: the payment date, **Paid from** account, check number or reference, memo and how much goes to each of the vendor's bills (the vendor stays). Say **Why are you changing it?** and **Save changes**. OpenBook reverses the payment's journal entry on its original date, posts the corrected one and pays the bills again; each change is listed under **Changes** on the payment.

- A check that's been printed, sent by ACH or included in a positive pay file keeps its amount, account and number; which bills it pays can still change.
- A payment matched to a bank feed line keeps its amount and account.
- A payment reconciled at the bank can't be changed, as it can't be voided.

## Expenses

To record something you've already paid, go to **Banking**, find the bank or card account it came out of and choose **Check / payment** (or **Card charge** for a card).

- **Vendor** (optional): links the expense to the vendor, so it shows on their page and in 1099 totals, and fills in their default category.
- **Payee**: who you paid, if not a vendor you keep on file.
- **Date** and **check number** or **reference**.
- One **category** line per account, with an amount. Add lines to split a receipt, for example office supplies and a meal on one card charge.
- **Save**.

Most expenses don't need typing at all: import your bank or card statement and add them from the review list ([Banking](06-banking.md)). Bank rules can fill in the vendor and category for you.

To correct an expense, open it and use **Change** ([Correcting a transaction](06-banking.md#correcting-a-transaction)).

### Paid with your own money

Bought something for the business with your own card or cash? Use **Banking → Paid with your own money** (or the same button on **Write Checks**). It opens the expense form paying from "Owner Contributions", an equity account OpenBook adds the first time: the cost goes to its category and counts as money you put into the business. Vendors, categories and 1099 totals work as for any expense. To pay yourself back later, record a transfer or a check from the bank to an owner's draw account. Through the API, any equity account or a loan from the owner (an other current or long-term liability) can be the pay-from account.

## Deposits paid to vendors

When a vendor wants money before their bill (a deposit on an order), use **Purchases → Vendor deposit**: the vendor, amount, date, the bank or card account it's **paid from**, and if you like the **purchase order** it's for and a reference. It goes into "Vendor Deposits", an asset account OpenBook adds the first time, because it's still yours until the goods come.

When their bill arrives, open the deposit and **Use it on bills**: it lists the vendor's unpaid bills with the deposit spread over them, oldest first; change the amounts if you like and **Pay these bills from the deposit**. Each becomes an ordinary bill payment from "Vendor Deposits" (reference "Deposit …"), so the bill's balance, the A/P aging and cash-basis reports all follow; on cash basis the cost counts on that date. The deposit page shows what's used and what's left. To take some back off a bill, void that payment. A deposit can be voided only while none of it is used.

## Vendor credits

A *vendor credit* reduces what you owe a vendor: a return, a refund promised for a billing mistake.

**Purchases → Vendor credit**: choose the vendor, enter their credit number, the date and the category lines (usually the same accounts as the original bill). Then either:

- **Apply to bills**: use it against their open bills, which lowers what's left to pay, or
- if they pay you the money back, use **Record a refund from the vendor** on the credit's page.

**Sending stock back**: on a line, choose the inventory item and the **quantity** going back, with the amount they credit. The stock leaves at its cost (oldest first, or the average for average-cost items), and if the credit is more or less than that cost, the difference goes to "Purchase Price Variance" (added the first time). Items tracked by lot or serial number go back with a quantity adjustment naming them instead. A credit that sends stock back can't be changed; void it (which puts the stock back) and enter it again.

To correct a vendor credit (one without stock on it), use **Change** at the top of its page: the credit number, date, lines, location and memo (the vendor stays). Say **Why are you changing it?** and **Save changes**. OpenBook reverses its journal entry on its original date and posts the corrected one, keeping what's applied and refunded; each change is listed under **Changes**. The new total can't be less than what's already applied and refunded, the date can't be later than the day it was first applied, and if it was refunded in a closed period, changing its lines needs the closing date password.

## Where it shows up

- The **Purchases** page lists recent bills, payments, expenses and vendor credits. **Unpaid bills** shows every unpaid one; the other lists show the newest 100, with **Show 100 more** below.
- **Reports → A/P aging** shows what you owe and how overdue it is; **Unpaid bills** lists each one.
- **Reports → 1099 summary** totals what each tracked vendor was paid in a year (bill payments and expenses, not card payments, which the card company reports). Paying for labor and materials on one check? Tick **Not reportable on 1099s** on the materials account (or merchandise, freight): bill lines and expense categories on it come off the vendor's total, a bill payment in proportion to the bill's lines, and the summary shows what was left out.
  Refunds a vendor made in the year show in their own column. They aren't taken off the total unless you tick **Take refunds from a vendor off what's reported for them on 1099s** in the purchasing settings (**Bills to approve → Settings**): whether to report the net figure is a question for your tax adviser. The filing data on **Accountant tools** takes a threshold for years whose figure has been published, and warns about vendors over it with no tax ID.
  **Copies and filing**, under the table (accountant role): **Download filing data** for the IRS's IRIS system or your filing service, **Vendors' copies (PDF)** to print, **Email vendors their copies** (only with a vendor's agreement to get it by email), and **Mark as filed** to note the date. OpenBook doesn't file with the IRS. If payments change after filing, the vendor's row shows the amount filed, so you can tell a corrected form is needed.
  **Paid before OpenBook**, under the table (accountant role): if you started using OpenBook part-way through a year, enter what each vendor was paid by cash, check or bank in your old books. It's added to their total and shown beside it; zero removes it. When history moved in from another program has journal entries dated in the year, the report says so, since payments inside them aren't counted.
  Where a vendor needs a form and has no tax ID, **Ask for a W-9** emails them the request, and in January the home page's to-do list counts them. The payer's number on the forms is your **Employer ID (EIN)** under **Settings → Company details**.
- The **profit and loss** shows the costs under each category.
