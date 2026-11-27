# Trucking settlements

For owner-operators: a carrier finds the loads, pays you a share of each, takes out the truck payment, fuel, insurance and the rest, and sends the difference to your bank once a week with a **settlement statement**. This chapter turns that statement into your books.

## Turning it on

- **A trucking company:** choose **Trucking** under **Which menu suits your work?**, at setup or in **Settings → Company details**. Settlements come first in the menu and on the home page.
- **Any other company:** tick **A carrier pays us by settlement statement** under **Trucking** in **Settings → Company details**. Settlements are added to the menu you have.

Either way it only changes what's shown. The page is at **Settlements** (`/settlements`).

Then, on **Carriers and codes** (accountant role):

1. **Add the usual trucking accounts** adds the accounts settlements need that your chart doesn't have yet, such as Freight Revenue, Fuel, Truck Rent and Tire Reserve.
2. **Add a carrier**: its name, your truck or unit number, and the bank account it pays into. A **vendor record** makes what it takes out show under that vendor in **Expenses by vendor**. A **class** (when you use classes) is usually the truck, so you can see each truck's profit.

## Entering a statement

**Settlements → Enter a settlement.** Choose the carrier, and type the **statement number** (its date will do when it has none), the day you were **paid** and the **net paid** to your bank.

Then the lines, with amounts as the statement prints them:

| Part | What goes there | Sign |
| --- | --- | --- |
| Revenue | Load pay, fuel surcharge, tolls and tarps paid to you | As printed |
| Reimbursements | Money given back: repairs advanced, a reserve paid out | As printed |
| Deductions | Truck payment, fuel, insurance, what goes into reserves | Positive takes money; a minus gives it back, like a fuel discount |
| Wages | A second driver's pay and its taxes charged to you | Positive takes money |
| Other | Reserves by the mile, interest, a balance from last week | With a minus when it's taken from you |

**Load**, **Dispatch**, **Loaded miles**, **Empty miles** and **Gallons** are kept for the reports; they don't go into the books. **Paste lines from a spreadsheet** adds many at once: rows copied with a header can have their columns in any order.

**Import a CSV file** on the Settlements page does the same from a file, for a carrier that gives you one or a spreadsheet saved as CSV; a settlement number column makes a settlement of each. Ones already entered are left out. If the file doesn't say the net paid, **Change** the settlement and type it from the statement.

Under **What the statement says, to check against**, you can type the total printed for each part, and the balances it prints for reserves and advances. They're compared, never refused.

Below the lines you see what each part comes to and how much is **still to find**. You can save a half-finished settlement; it can't be posted until the lines add up to the net paid and anything carried forward.

Under **More options**:

- **Carried forward**: when a week doesn't cover its charges, enter 0 as the net paid and what you still owe with a minus. It's kept in "Due to Carrier", and next week's *balance forward* line (in **Other**) clears it.
- **Paid into more than one account**: when the carrier splits your deposit.
- **Choose accounts for single lines** (under the lines): an account for one line instead of its code's.

## Reading the statement from its PDF

**Settlements → Read a statement.** Choose the carrier, the day you were paid and the file (a PDF or a picture), then tick the pages that are the settlement itself. **Only those pages are read and kept**: statements often have other people's pay stubs, bank details and invoices further on.

- **Read with Claude** sends those pages to Anthropic with your company's API key. An admin switches it on first, under **Settings → Document reading**. It reads scans well.
- **Read it on this computer instead (backup)** sends nothing anywhere. A PDF that holds its own text is read exactly. A scan is read by an OCR program (Tesseract), if whoever runs OpenBook has set one up, and **it makes mistakes**: an 8 for a 3, a lost decimal point or minus sign, a missing line. On one real statement it misread an amount, a code and several words; another may go worse.

What's read becomes a settlement waiting to be checked, with the pages attached. Nothing is posted.

**After a scan**, the settlement's page shows the pages and asks you to **check the scan against the statement**:

1. Lines marked **Check this** are the ones the reading wasn't sure of, or whose part doesn't add up to its printed total. Tick each one that's right; use **Change** to fix the others.
2. Type the **net paid** from the statement. It isn't taken from the scan, so it checks it.
3. Tick that you **checked every line**, marked or not, and **Save the check**. Lines that add up aren't proof: two mistakes can cancel.

A scan can't be posted before that, and changing a line asks for it again. Reserves by the mile, interest and balances brought forward aren't read on this computer: add them in **Other**.

## Giving codes their accounts

Statements mark each line with a code (RV, UF, NP…). The first time one turns up, the settlement's page lists it under **Codes without an account** with a suggestion from its words. Check each one and **Save the accounts**: it's remembered for every settlement from that carrier. The same code can mean different things in different parts of the statement, so each part has its own.

Change or forget them later on the carrier's page. Settlements already posted keep their entries.

**Reserves are still your money.** What goes into a tire reserve, emergency fund or performance bond belongs on an asset account, not an expense: it's an expense when it's spent. **Advances aren't income.** When the carrier pays a repair and takes it back over several weeks, both the advance and the paybacks go to "Carrier Advances", and the repair itself is the expense. That's why your profit can differ from the statement's. Ask your accountant if your contract says a reserve isn't returned.

## Checking it

The settlement's page has **Check it**, which compares it with the last three:

- **New** codes, and codes **missing** that were on every one of them.
- Amounts that were the same each week and **changed**, such as the truck payment.
- Each part's total beside last time's and, if you typed them, the statement's.
- **Reserves and balances**: what your books say you have in the tire reserve, say, beside what the statement says. A difference that stays the same is usually a starting balance you haven't entered ([Moving in](24-moving-in.md)).
- A line going to an account on the **Loans** page, whose schedule wouldn't see it.

Nothing there stops it posting.

**Were you paid your share?** On the carrier's page (accountant role), type **your share of each charge** from your contract (72 for linehaul, say) and choose which charge each code pays under **Pays a load's**. Then, on a settlement, open **What the loads' customers were billed** and type the charges from the freight bills that came with it. **Loads against what was billed** shows what you should have been paid, what you have been on every settlement so far, and what's **short**: detention billed and not passed on, say. Type a load's miles beside its linehaul too and they're compared with the miles you were paid for, since a load split between dispatches can lose some.

## Posting

**Post settlement** makes one journal entry: the net to your bank, and each line's amount to its account. When the deposit shows in your bank feed, match it to this one rather than adding it. If the deposit was already entered as received, the page points it out: void that first, or the money counts twice.

To change a posted settlement, **Void the entry**; it goes back to waiting, where **Change** works. **Dismiss** a statement that's a duplicate or was issued again.

Notes and files (the statement itself, say) can be kept with each settlement, at the bottom of its page. Statements often show other people's pay, so attach only what you need.

## Receipts you paid on the road

Fuel on your own card, a scale ticket, a lumper, a tire repair: what the carrier didn't pay and take back goes in **Settlements → Receipts → Enter a receipt**. Type in what's on the paper receipt:

- **What for**: fuel, diesel exhaust fluid, tolls, scales, lumper, repairs, tires, parking or other.
- **Date**, **Amount paid**, and for fuel and DEF the **Gallons**.
- **Bought at** (the truck stop or shop), the **City** and the **State or province**. Fuel needs the gallons and the state: a fuel tax return counts the gallons bought in each one.
- **Paid from**: the bank account, card or cash account the money came from.
- **Account**: leave it on **The usual one for what it's for** and fuel goes to Fuel, tolls and scales to Tolls and Scales, repairs to Truck Repairs and so on (the accounts **Add the usual trucking accounts** makes, or ones named like them). Once you choose another for a kind, its next receipts go there too. Lumpers and other things have no usual account until you choose one.
- **Truck (class)**, when you use classes: it starts on the last receipt's truck, or the carrier's class.
- A **Vendor record**, a **Receipt number** and a **Memo** if you like, and a **Photo of the receipt**, kept on the expense in case you're asked for it.

**Save and enter another** keeps the kind, date, account and truck for the next one, for a stack of receipts from one trip. If something is missing, the form comes back with what you typed (choose the photo again).

Each receipt is recorded as an expense from the account you paid with, so it matches the line on your bank or card statement and shows in the **Operating statement** under its truck. The expense's page says which receipt it came from. To correct one, **Void** it on the receipt's page and **Copy** it to enter it again, or change the amount, date or account on the expense itself.

**Settlements → Receipts** lists them by period (**This quarter**, **Last quarter**, or any dates), with the price per gallon, and **Fuel by state**: the gallons and money of the fuel receipts in each state or province. A fuel tax (IFTA) return also needs the miles driven in each state, which OpenBook doesn't keep.

Photographed receipts can wait in **Purchases → Receipts** (see [The document inbox and receipt capture](16-document-inbox.md)); open one and choose **Record it as a trucking receipt** to keep its gallons, state and truck. Its photo goes on the expense.

## Reports

Under **Reports → Trucking**:

- **Operating statement**: income and expenses for a period, the year to date and all dates, each with its amount **per mile**, and the loaded and empty miles. Filter by class for one truck. A posted settlement's page opens it for that week. Expenses you enter elsewhere, such as fuel on your own card, are in it too. Per mile counts from your first settlement, so books from before then don't distort it.
- **Loads**: each load's miles, pay and own charges (its fuel, its fuel tax), and what was left, per mile. Rent, insurance and reserves belong to no one load and are on a line of their own.
- **Settlement codes**: what each code came to and where it went. Over a year, compare its revenue with the carrier's 1099.
- **Reserves and advances**: what went into and out of each, and the balance the statement gave.

## What it doesn't do

It isn't payroll: a second driver's wages and taxes on the statement go to the accounts you choose, and no pay stubs or W-2s are made. Fuel tax (IFTA) returns aren't prepared, though the gallons are kept, and the gallons you bought are added up by state under **Receipts**.
