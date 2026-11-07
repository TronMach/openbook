# Bank connections, checks, positive pay and petty cash

More tools linked from the **Banking** page, beyond the everyday ones in [Banking](06-banking.md).

## Bank connections

Instead of downloading statement files, connect your bank accounts so new transactions arrive by themselves. OpenBook uses [SimpleFIN Bridge](https://beta-bridge.simplefin.org/), which links to thousands of US and Canadian banks for a small yearly fee.

**Banking → Bank connections** (accountant role):

1. At SimpleFIN Bridge, connect your banks, then create a **setup token**.
2. In OpenBook, paste it under **Connect with SimpleFIN**, give the connection a name, and **Connect**. The token works once; OpenBook swaps it for a key it stores encrypted.
3. On the connection's page, each account at the bank is listed. Choose which of your accounts each one **goes to**, and the date to **fetch from**. **Save**.

OpenBook then fetches posted transactions every six hours (or **Sync now**) into each account's review list, where you add or match them as with an imported file. Pending transactions wait until they post. Overlaps are recognized, so nothing arrives twice. If a sync fails, the connection's page says why. **Disconnect** stops it.

## Printing checks

Payments from bank accounts (bill payments, checks written as expenses, and refunds to customers, of credit or of a retainer) can be printed on standard **voucher check stock**: the check at the top and two stubs below, with the bank details, MICR line and signature line already printed on the stock.

**Banking → Print checks**: choose the **account** (or all), show checks **not printed yet** or all, tick the ones to print and **Print selected**. The PDF opens in a new tab. Payments with a check number start ticked. Payments sent by ACH aren't listed, nor are spends that came from your bank's lines, because that money has already left.

- Load the stock face up.
- Print at **100% ("actual size")**, not "fit to page".
- Reload the page afterwards to see them marked as printed.

A single check can also be printed from its payment, expense or refund page (a retainer's refunds have the button on the retainer). Refund check numbers count toward the next number suggested, like any other check. The amount is written out in words.

## Positive pay

Many banks protect business accounts from check fraud with *positive pay*: you send the bank a list of the checks you've issued, and it refuses any check not on the list.

**Banking → Positive pay**:

1. Choose the **account**. With the dates left blank, it lists everything the bank hasn't been told yet: new checks and voids of checks it already has, whatever their date. Enter **from** and **to** dates and **Show checks** to send those dates' checks again. Voided ones are marked V.
2. Under **Issue file**, fill in the **account number at the bank**, the **date format** your bank wants, and whether to **include a header row**. OpenBook remembers these for the account.
3. **Download CSV** and upload it to your bank.

The file has the account number, check number, issue date, amount, payee and void flag on each line; customer and retainer refunds by check are on it too. Spending that came from a statement or bank feed isn't listed, whatever its reference says: that money has already left. The first file for an account goes back 90 days, because older checks have long cleared; use the dates to send older ones.

## Petty cash

A cash box for small purchases, kept at a fixed *float* (say $200) and topped up when it runs low.

**Banking → Petty cash → New petty cash box**:

- **Account**: an existing bank account for the box, or **create a new account** with a name.
- **Float**: the amount the box should hold.
- **Custodian**: who looks after it (optional).
- **Fund it from** a bank account on a date, which records the transfer into the box; or **don't record a transfer** if it's already funded in your books.

### Spending from the box

Record each purchase as an ordinary expense from the petty cash account (**Check / payment** on the Banking page), with the receipt attached.

### Counting

On the box's page, **Count the cash**: the **date** and the **cash counted**, and optionally the **notes and coins** as value x count (`20x5; 1x3; 0.25x8`). **Record count**. If the count differs from the balance in the books, OpenBook records the difference to "Cash Over and Short", so the box matches what's really there.

### Replenishing

**Replenish** transfers the float less the balance from a bank account into the box, bringing it back to the float. The page shows how much that is today.

## Recurring expenses

For something paid on a schedule straight from a bank or card account, with no bill (a software subscription on the card, an insurance or loan autopay), set up **Banking → Recurring expenses → New recurring expense**:

- **Name**, the **Paid from** account (a bank or credit card account), the **vendor** (optional) and **payee**.
- **Repeat every** so many weeks, months, quarters or years, the **first payment date**, and optionally an **end date** or a number of **times**.
- The category lines, as on a bill, and a memo (the name when left blank). A location and tags can go on every expense it records.
- **Record expenses automatically when due**: OpenBook records each one in the hourly job. Otherwise they wait on the list until you select **Record due expenses**.

Each occurrence is an ordinary expense in the account's register, dated on its schedule; if one is refused (a closed period, an inactive account), the template stops there and says why. Changes to a template apply to future expenses only. For bills you pay later, use [recurring bills](05-purchases.md) instead.

