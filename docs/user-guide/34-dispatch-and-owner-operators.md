# Dispatch and owner-operators

For carriers: you book the loads, owner-operators haul them in their own trucks, and once a week you pay each one their share less what they owe you. This chapter covers the owner-operators, their trucks, and what they owe you between settlements. (If a carrier pays *you*, see [Trucking settlements](33-trucking-settlements.md).)

## Turning it on

Tick **We pay owner-operators and dispatch loads** under **Trucking** in **Settings → Company details**. **Dispatch** is added to the menu you have. It only changes what's shown.

## Owner-operators

Each owner-operator is a **vendor** first: add them on **Vendors**, with their tax ID. Tick **Track for 1099** there only if you send them a 1099: pay for hauling freight usually doesn't need one (see [The 1099](#the-1099)). Then, on **Dispatch → Owner-operators** (accountant role), **Add an owner-operator** and choose the vendor record.

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

For your own trucks, **Dispatch → Fixed costs by truck** adds up what each costs whether it hauls or not (its recurring expenses and bills, loan, depreciation) a week, a month and a mile, by its class. See [Trucking settlements](33-trucking-settlements.md), Reports.

## Loads and the load board

**Dispatch → Load board** shows every load not yet invoiced, by where it is: **Booked**, **Dispatched**, **In transit** and **Delivered**. **Every load** lists them all, cancelled and invoiced ones too.

**Dispatch → New load** (standard role):

- The **customer**, their **load number** from the rate confirmation, and the loaded **miles**; commodity, weight and a memo if you like.
- **Stops**: at least one pickup and one delivery, each with the shipper or receiver and where, their pickup or delivery number, and the appointment (with its window's end, when there is one).
- **What the customer is charged**: linehaul, fuel surcharge, tolls, detention, tarps or other, each a quantity times a rate (linehaul by the mile is the miles times the rate per mile). The **item** is what the invoice will use.
- **Trucks**: the truck, its driver, and its loaded and empty miles. A load split between trucks gets a row each, with each truck's **share**; OpenBook says when the shares don't add up to 100% or the trucks' loaded miles don't add up to the load's.

A load with a truck is **Dispatched**. On the load's page, save when the truck **Arrived** at and **Left** each stop: leaving the first pickup puts the load **In transit**, leaving the last delivery **delivers** it, and the time spent at each stop is shown. Or set where it is by hand under **Where it is**, which is also where a load is **Cancelled** (and restored).

A load posts nothing in the books: its invoice does, and the owner-operators' settlements pay for it. Attach the rate confirmation, bill of lading and proof of delivery on its page.

### Invoicing a load

Once a load is **Delivered**, its page has **Invoice the customer**: choose the date and OpenBook makes the invoice, a line for each charge with its item, quantity and rate, and the customer's load number in the memo. Each charge needs its item first (change the load to choose them). The load then leaves the board. If that invoice is voided, the load can be changed and invoiced again.

### Detention

When the truck's times at a load's stops are saved, its page shows how long it spent beyond the free time at each, and **Charge detention** adds a detention charge for those hours (rounded up to the quarter hour) at the rate per hour you give, before you invoice. Charged again, it replaces the one there is.

### Pay for loads

On the owner-operator's page, **Pay for loads** says how they're paid for the loads their trucks deliver:

- **A percent of linehaul**, **by the mile** (the truck's own loaded miles, and its empty miles too if you tick it) or **a flat amount a load**. **As typed into each run** leaves it to you.
- Their share, in percent, of the customer's **other charges**: fuel surcharge, tolls, detention, tarps, other. Tolls are paid as a reimbursement (not on the 1099), the rest as earnings.

The weekly run then pays each delivered load once, for each of the owner's trucks on it: a load split between trucks pays each truck its share. A load whose truck left the last delivery after the run's **Through** date waits for the next run. A load paid on a settlement can't have its truck changed or be cancelled until that settlement is voided.

## Charges and advances

**Dispatch → Charges and advances.** Anything an owner-operator owes you waits here until a settlement takes it back: fuel on your fuel card, tolls, a repair you paid the shop for, or an **advance** of money.

**Add a charge or advance**: the owner-operator, the kind, the date and the amount, and **Paid from**, the account the money came from: the bank for an advance, the fuel card's account for fuel. OpenBook posts it as its own entry, moving the amount into the owner's receivable account. When the money is already in the books on that account (a shop's bill entered with the receivable account as its category, say), choose **Already in the books**: nothing more is posted.

Tick **already counted on the owner's 1099** only when the money went to the owner as an expense that's already on their 1099; taking it back then doesn't count again.

A charge nothing has taken back yet can be voided on its page: its entry is reversed on the same date.

### Importing fuel and tolls

**Dispatch → Import fuel and tolls** takes a fuel card's or toll account's export as a CSV file. It needs columns for the **date**, the **truck** (its unit number, or its plate or tag) and the **amount**; gallons, the location and a reference (a transaction or ticket number) are kept when the file has them. Lines above the header, such as a title, are skipped.

Choose **what's in it** (fuel, tolls, repairs or other charges) and **Paid from**, the fuel card's or toll account's own account (usually a credit card or an other current liability). Each row for an owner-operator's truck becomes a charge to them. Rows are left out, and counted, when they're for one of your own trucks, for a truck OpenBook doesn't know (add it, with its unit or plate as the file writes it, and import again), when they're a credit or refund, or when the owner already has a charge for the same date, amount and reference, so importing the same file twice adds nothing.

Settlements take charges back oldest first, as far as each week's pay allows. A settlement never pays less than nothing: what doesn't fit waits for the next one.

## Paying owner-operators each week

**Dispatch → Pay owner-operators** (standard role).

1. Choose the period: **Through** is its last day (charges dated after it wait for the next run) and **Bill date** the day the bills are dated.
2. For each owner-operator, enter what they **earned** (a line per load, with its load number, truck and miles if you like) and any **reimbursement** (tolls or scales they paid that you give back). Earnings count on their 1099 when you send one; reimbursements don't, so keep the receipts they give you for them. To pay money out of a reserve, type how much beside it. Tick **Hold** to leave someone out this time.
3. **Preview** shows each settlement as it will be made: the pay, then what's taken out.
4. **Make the settlements**: each owner-operator gets one bill, numbered **Settlement 1**, **Settlement 2** and so on, to pay on **Pay bills** by check or ACH like any other.

What's taken out, in this order and only as far as the pay goes:

Their pay is what their loads earned by their pay rule (see Pay for loads), plus what you type. What's taken out, in order:

1. **Backup withholding**, when it's turned on for their vendor record (24% of what counts on the 1099). It applies only to pay that has to go on a 1099, so pay for hauling freight usually has none: turn it on only when it does.
2. **Regular deductions**, in their order.
3. **Charges and advances**, oldest first.
4. **Reserves**, by their own rule, up to their target.

A settlement never pays less than nothing: whatever doesn't fit stays open for the next one, and the preview says so. A week that comes to exactly zero has no bill (bills must be above zero); it posts its own entry instead.

Bills from settlements wait for approval like any other when your **approval limit** (Settings → Purchasing) is set. For a settlement, the limit is compared with the **gross pay**, not the net, since that's what the owner earned.

### The statement, and paying by ACH

On a settlement's page, **Statement PDF** is the owner-operator's settlement statement: each line under its part (earnings, reimbursements, deductions, charges taken back, reserves), the net pay, and their reserves and the charges they still owe after it. **Email** sends it to them, from a template you can change in **Settings → Email templates** (Owner settlement).

To pay owner-operators by direct deposit, enter their bank details on their vendor page as for any vendor, and tick **A person's own account** when it's their personal account rather than a business's: ACH files then put those payments in a **PPD** batch, beside the **CCD** batch for businesses, as banks expect.

### Correcting a settlement

A settlement's bill can't be changed or voided on its own. Open the settlement (**Dispatch → Owner settlements**) and **Void** it: its bill is voided on the same date, what it took back of charges is open again, what its deductions left as charges is gone, and its reserves and deductions are as they were. Then run it again. Only an owner-operator's **latest** settlement can be voided, because each one starts from what the one before took and left; void the later ones first. A bill with a payment on it can't be voided: void the payment first.

### The 1099

**Do you need to send one?** Usually not, for hauling. IRS rules leave payments for freight off Forms 1099-NEC and 1099-MISC (Treasury Regulation § 1.6041-3), and the IRS has said this covers paying independent truckers to haul goods (Information Letter 2001-0101). Many carriers send owner-operators a 1099 anyway, as a record that they're contractors, not employees. Pay for other work, such as unloading, installing or final-mile service, may need one. Neither the rule nor the IRS letter gives a test for that.

OpenBook counts an owner-operator's 1099 only when **Track for 1099** is ticked on their vendor record. When it is, the 1099 counts their **gross** earnings, not what you paid after deductions: OpenBook adds what each settlement took out of the gross (deductions, charges, reserves and withholding) on the settlement's date, and the bill's payment counts the rest when it's paid. Reimbursements and money paid out of a reserve don't count. A charge marked **already counted on the owner's 1099** isn't counted again when it's taken back. Backup withholding shows in box 4 and on the **Backup withholding** report.

Reserves count in the year they're held back from the pay, since that money is the owner's; paying it out later doesn't count again. Interest on reserves goes on a 1099-INT (see Reserves above). If you're unsure about either, or whether your work is all hauling, ask your accountant before you file. OpenBook doesn't check settlements against federal leasing rules.

## The books check

**Owner receivables match the open charges** (in the books check) compares what's still open on the charges with the owner receivable accounts, and **Owner reserves match the settlements** what settlements and interest put into the reserves with the reserve accounts. A journal entry made straight to those accounts is listed, so you can see what it was.
