# Dispatch and owner-operators

For carriers: you book the loads, owner-operators haul them in their own trucks, and once a week you pay each one their share less what they owe you. This chapter covers the owner-operators, their trucks, and what they owe you between settlements. (If a carrier pays *you*, see [Trucking settlements](33-trucking-settlements.md).)

## Turning it on

Tick **We pay owner-operators and dispatch loads** under **Trucking** in **Settings → Company details**. **Dispatch** is added to the menu you have. It only changes what's shown.

## Owner-operators

Each owner-operator is a **vendor** first: add them on **Vendors**, with their tax ID, and tick **Track for 1099** there. Then, on **Dispatch → Owner-operators** (accountant role), **Add an owner-operator** and choose the vendor record.

Three accounts decide where their settlements go. Leave them as they are and OpenBook makes them the first time:

| Account | Type | What's in it |
| --- | --- | --- |
| Owner-Operator Pay | Cost of goods sold | What they earn on their loads, before anything is taken out |
| Owner Receivables | Other current asset | Fuel, tolls, repairs and advances they owe you, until a settlement takes them back |
| Owner Reserves | Other current liability | Escrow and reserves held back from them, which stay theirs |

A **class** (when you use classes) counts their lines under it in reports by class; a truck's own class comes first.

### Reserves

On the owner-operator's page, under **Reserves**, **Add a reserve** for each fund held back from their settlements, such as a maintenance escrow or a plate reserve:

- **Added**: a fixed amount each settlement, an amount per mile, or a percent of what the settlement earned.
- **Up to**: once the balance reaches it, nothing more is added. Leave it blank for no limit.
- **Already held before OpenBook**: what they had in it when you started. Its money should already be in the reserve account, from an opening balance or a journal entry.
- **Yearly interest %**: when you pay interest on what you hold.

The table shows each reserve's balance. A reserve can be stopped (untick **Still added to**), but never removed, since its history counts toward the balance.

**Interest.** Reserves with a yearly interest rate earn a month's interest on their balance at the month's end. On **Dispatch → Interest on reserves**, choose the month and **Add the interest**: OpenBook posts one entry on the month's last day, an expense ("Interest Paid on Owner Reserves" unless you choose another) against each owner's reserve, and adds it to their reserve. Once a month; void it there to do it again. The interest isn't pay, so it isn't on the owner's 1099-NEC (interest of $10 or more a year goes on a 1099-INT, which OpenBook doesn't prepare).

### Regular deductions

Under **Regular deductions** on the owner-operator's page, **Add a deduction** for anything taken from every settlement by agreement: insurance, a truck payment, a fee.

- **Goes to**: an income account for a charge you make (insurance you charge back), or an asset for a balance the owner is paying you off (a truck they're buying from you).
- **Worked out**: a fixed amount each settlement, or an amount per mile on it.
- **Stops after taking**: for a balance being paid off, or a target. Blank for no end. **From** and **Until** limit it to dates.
- **Order**: lower numbers are taken first when the pay doesn't cover them all.
- **What a settlement can't take is left as a charge for the next one**: ticked, a week too small leaves the rest as an open charge, taken back like any other; unticked, the rest is simply not taken.

The table shows what each has taken so far.

## Trucks

**Dispatch → Trucks.** Each truck has a **unit number**, and an **owner-operator** unless it's one of yours. A **class** for the truck lets reports by class show what each truck earned and cost. Year, make, VIN and plate are for your records.

## Charges and advances

**Dispatch → Charges and advances.** Anything an owner-operator owes you waits here until a settlement takes it back: fuel on your fuel card, tolls, a repair you paid the shop for, or an **advance** of money.

**Add a charge or advance**: the owner-operator, the kind, the date and the amount, and **Paid from**, the account the money came from: the bank for an advance, the fuel card's account for fuel. OpenBook posts it as its own entry, moving the amount into the owner's receivable account. When the money is already in the books on that account (a shop's bill entered with the receivable account as its category, say), choose **Already in the books**: nothing more is posted.

Tick **already counted on the owner's 1099** only when the money went to the owner as an expense that's already on their 1099; taking it back then doesn't count again.

A charge nothing has taken back yet can be voided on its page: its entry is reversed on the same date.

Settlements take charges back oldest first, as far as each week's pay allows. A settlement never pays less than nothing: what doesn't fit waits for the next one.

## Paying owner-operators each week

**Dispatch → Pay owner-operators** (standard role).

1. Choose the period: **Through** is its last day (charges dated after it wait for the next run) and **Bill date** the day the bills are dated.
2. For each owner-operator, enter what they **earned** (a line per load, with its load number, truck and miles if you like) and any **reimbursement** (tolls or scales they paid that you give back). Earnings count on their 1099; reimbursements don't. To pay money out of a reserve, type how much beside it. Tick **Hold** to leave someone out this time.
3. **Preview** shows each settlement as it will be made: the pay, then what's taken out.
4. **Make the settlements**: each owner-operator gets one bill, numbered **Settlement 1**, **Settlement 2** and so on, to pay on **Pay bills** by check or ACH like any other.

What's taken out, in this order and only as far as the pay goes:

1. **Backup withholding**, when it's turned on for their vendor record (24% of what counts on the 1099).
2. **Regular deductions**, in their order.
3. **Charges and advances**, oldest first.
4. **Reserves**, by their own rule, up to their target.

A settlement never pays less than nothing: whatever doesn't fit stays open for the next one, and the preview says so. A week that comes to exactly zero has no bill (bills must be above zero); it posts its own entry instead.

Bills from settlements wait for approval like any other when your **approval limit** (Settings → Purchasing) is set. For a settlement, the limit is compared with the **gross pay**, not the net, since that's what the owner earned.

### Correcting a settlement

A settlement's bill can't be changed or voided on its own. Open the settlement (**Dispatch → Owner settlements**) and **Void** it: its bill is voided on the same date, what it took back of charges is open again, what its deductions left as charges is gone, and its reserves and deductions are as they were. Then run it again. Only an owner-operator's **latest** settlement can be voided, because each one starts from what the one before took and left; void the later ones first. A bill with a payment on it can't be voided: void the payment first.

### The 1099

An owner-operator's 1099 counts their **gross** earnings, not what you paid after deductions: OpenBook adds what each settlement took out of the gross (deductions, charges, reserves and withholding) on the settlement's date, and the bill's payment counts the rest when it's paid. Reimbursements and money paid out of a reserve don't count. A charge marked **already counted on the owner's 1099** isn't counted again when it's taken back. Backup withholding shows in box 4 and on the **Backup withholding** report.

**Have your accountant confirm this treatment before you file.** OpenBook doesn't check settlements against federal leasing rules.

## The books check

**Owner receivables match the open charges** (in the books check) compares what's still open on the charges with the owner receivable accounts, and **Owner reserves match the settlements** what settlements and interest put into the reserves with the reserve accounts. A journal entry made straight to those accounts is listed, so you can see what it was.
