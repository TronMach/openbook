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

**Purchases → Vendors → New vendor** (or `n` then `v`):

- **Display name** (unique), **company**, **email**, **phone** and **address**.
- **Your account number with them** is used as the memo on bill payments, so it prints on the check.
- **Payment terms** set the due date of their bills.
- **Default category** is the account their bills and expenses start with, for example "Rent" for your landlord.
- **1099**: tick **Track payments for a 1099** for contractors, landlords, attorneys and others you report to the IRS, choose the form and box, and enter their **tax ID** from their W-9. After saving, the tax ID is only shown masked (last four digits).

Vendors with unpaid bills can't be made inactive. Vendors can be imported from a CSV file with **Vendors → Import CSV**.

## Bills

**Purchases → New bill** (or `n` then `b`):

1. Choose the **vendor**. Their terms fill in the **due date**, and their default category fills in the first line.
2. Enter the vendor's **bill no.** (their invoice number; OpenBook won't let you enter the same number twice for one vendor) and the **bill date**.
3. Add a line per category: the **account** the cost belongs to (Office Supplies, Rent, Cost of Goods Sold…), a description and the amount. A negative amount is a credit on the bill.
4. For stock you buy, add **item lines** instead: the inventory item and quantity. They add to stock on hand at the line's cost.
5. **Save bill**.

Attach the vendor's PDF or a photo to the bill under **Attachments** on its page, so it's there when you need it.

To correct a bill, use **Change** at the top of its page: the bill number, dates, lines, location, warehouse and memo. Say **Why are you changing it?** and **Save changes**. OpenBook reverses the bill's journal entry on its original date and posts the corrected one, so the bill keeps its payments and history; each change is listed under **Changes** on the bill.

- The vendor can't change: to bill a different vendor, void the bill and enter a new one.
- If part of the bill is paid or credited, the new total can't be less than that. Changing what a bill paid in a closed period was for needs the closing date password, like any change dated in a closed period.
- Stock the bill brought in is received again, so the change is refused once some of it has been sold, used, moved or given landed costs.
- Lines marked billable to a customer stay as they are, on the same line; remove the mark first to change one.
- If the bill is over the approval limit after the change, it waits for approval again, even if it was approved before, and the person who changed it can't approve it (unless self-approval is on).
- Bills entered from a purchase order can't be changed yet: void them and enter them from the order again.

A bill can be voided (with a reason) from its page until a payment is made against it.

Bills that arrive regularly with the same amounts, such as rent, can be entered automatically: see **Purchases → Recurring**.

## Paying bills

**Purchases → Pay bills** lists every unpaid bill with its due date; overdue ones are highlighted.

1. Choose the account to **pay from**: a bank account (checks, transfers) or a credit card.
2. Set the **payment date**.
3. For a bank account, enter the **first check number**. OpenBook writes one payment per vendor and numbers the checks upward from it. For a card, enter a reference.
4. Tick the bills to pay. Change the amount on a row to pay part of a bill. Use **Show bills from** to narrow the list to one vendor.
5. Select **Pay** (it shows the total).

To print checks, go to **Banking → Print checks**. Bills on hold for approval are listed unticked and can't be paid until approved.

## Expenses

To record something you've already paid, go to **Banking**, find the bank or card account it came out of and choose **Check / payment** (or **Card charge** for a card).

- **Vendor** (optional): links the expense to the vendor, so it shows on their page and in 1099 totals, and fills in their default category.
- **Payee**: who you paid, if not a vendor you keep on file.
- **Date** and **check number** or **reference**.
- One **category** line per account, with an amount. Add lines to split a receipt, for example office supplies and a meal on one card charge.
- **Save**.

Most expenses don't need typing at all: import your bank or card statement and add them from the review list ([Banking](06-banking.md)). Bank rules can fill in the vendor and category for you.

## Vendor credits

A *vendor credit* reduces what you owe a vendor: a return, a refund promised for a billing mistake.

**Purchases → Vendor credit**: choose the vendor, enter their credit number, the date and the category lines (usually the same accounts as the original bill). Then either:

- **Apply to bills**: use it against their open bills, which lowers what's left to pay, or
- if they pay you the money back, use **Record a refund from the vendor** on the credit's page.

To correct a vendor credit, use **Change** at the top of its page: the credit number, date, lines, location and memo (the vendor stays). Say **Why are you changing it?** and **Save changes**. OpenBook reverses its journal entry on its original date and posts the corrected one, keeping what's applied and refunded; each change is listed under **Changes**. The new total can't be less than what's already applied and refunded, the date can't be later than the day it was first applied, and if it was refunded in a closed period, changing its lines needs the closing date password.

## Where it shows up

- The **Purchases** page lists recent bills, payments, expenses and vendor credits.
- **Reports → A/P aging** shows what you owe and how overdue it is; **Unpaid bills** lists each one.
- **Reports → 1099 summary** totals what each tracked vendor was paid in a year (bill payments and expenses, not card payments, which the card company reports).
- The **profit and loss** shows the costs under each category.
