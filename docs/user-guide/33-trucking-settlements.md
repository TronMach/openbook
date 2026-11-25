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

## Posting

**Post settlement** makes one journal entry: the net to your bank, and each line's amount to its account. When the deposit shows in your bank feed, match it to this one rather than adding it. If the deposit was already entered as received, the page points it out: void that first, or the money counts twice.

To change a posted settlement, **Void the entry**; it goes back to waiting, where **Change** works. **Dismiss** a statement that's a duplicate or was issued again.

Notes and files (the statement itself, say) can be kept with each settlement, at the bottom of its page. Statements often show other people's pay, so attach only what you need.

## What it doesn't do

It isn't payroll: a second driver's wages and taxes on the statement go to the accounts you choose, and no pay stubs or W-2s are made. Fuel tax (IFTA) returns aren't prepared, though the gallons are kept.
