# Banking

The **Banking** page lists your bank and credit card accounts with their balances and what's waiting for review. From here you record money in and out, bring in your bank's statements, and reconcile.

To add an account, use **Add an account** and give it the type *Bank* or *Credit card*.

## Recording transactions by hand

Each account on the Banking page has:

- **Check / payment** (on a card: **Card charge**): money going out. See [Expenses](05-purchases.md#expenses).
- **Deposit** (on a card: **Card credit**): money coming in that isn't a customer payment, for example an owner's contribution, a loan, interest or a refund. Choose who it's from and the category (account) for each line.

**Transfer** (at the top of the Banking page) moves money between your own bank accounts, or pays a credit card from a bank account. It isn't income or an expense.

For customer payments, use **Receive payment** and **Make deposit** ([Customers and sales](04-sales.md)), not Deposit, so the invoice is marked paid.

## Importing your bank statement

Download transactions from your bank's website and import them, instead of typing each one.

1. On the Banking page, select **Import** on the account.
2. Choose the **statement file**. OpenBook reads:
   - **OFX, QFX or QBO** files (often called "Quicken", "Money" or "QuickBooks Web Connect" downloads). These are the most reliable.
   - **CSV** files, with a date, a description and either one amount column or separate money-out and money-in columns.
3. **Check how the file was read.** For CSV files OpenBook shows which columns it used, how it read the dates and which sign it took as money out. If every date in the file would make sense both month-first and day-first, it says so; check a few dates against your statement. Amounts it can't read with certainty are flagged rather than guessed.
4. Untick any lines you don't want, then **Send to review**.

Nothing is posted yet. Lines you've imported before are skipped, so overlapping downloads are safe.

### Connecting directly

Instead of downloading files, an accountant can connect accounts through SimpleFIN on **Banking → Bank connections**. OpenBook then fetches new transactions every few hours and puts them in the review list.

## Reviewing imported transactions

Select **Review** (or **… to review**) on the account. Each pending line can be:

- **Matched** to a transaction already in OpenBook with the same amount within 7 days: a check you wrote through Pay bills, a deposit you made from Undeposited Funds. When there are possible matches, a list shows them (pick the right one if there are several); select **Match**. Nothing new is posted, and the two are linked.
- **Added** as a new transaction: choose a **category** (and optionally a vendor or payee), then **Add**. Lines a bank rule recognized arrive with their category filled in and a "Rule" badge. Lines with a category and no match can all be added at once with the button above the list.
- **Excluded**, for duplicates or lines that don't belong in your books.

Done lines move to the Added, Matched and Excluded tabs. **Undo** puts a line back for review; for an added line this voids the transaction it created.

## Bank rules

Most lines repeat: the same utility company every month, the same software subscription. A *bank rule* fills them in for you. **Banking → Bank rules → New rule**:

- **When a line** has this text in its payee or memo, is money in or out, optionally on one account and within an amount range,
- **Then** give it this category, and optionally a payee and vendor (vendors only on money-out rules).
- **Order**: lower numbers are tried first; the first matching rule wins.
- **Add matching lines to the books automatically (no review)**: tick this only for rules you trust completely. Matching lines are then posted as they're imported. Otherwise the rule just pre-fills the line on the review list.

## Reconciling

Reconciling checks your books against the bank's statement, line by line, so you know they agree. Do it every month when the statement arrives.

1. On the Banking page, select **Reconcile** on the account.
2. Enter the **statement ending date** and the **statement ending balance** (for a card, the amount owed), then **Start reconciling**. The beginning balance is where your last reconciliation ended.
3. Tick each transaction that appears on the statement. The **difference** at the top goes down as you go. Your ticks are saved as you make them, so you can stop and come back.
4. When the difference is zero, select **Finish reconciliation**.

If the difference won't reach zero:

- Look for a transaction on the statement that isn't in OpenBook (bank fees and interest are common): add it, and it appears in the list.
- Look for one entered twice, or with the amount mistyped.
- Check the ending balance you typed.

You can **Discard** an unfinished reconciliation, or **Update statement** if you typed the date or balance wrong. Each finished reconciliation has a **reconciliation report** to print or keep.

Once a transaction is reconciled it can't be voided, because that would change a balance you've agreed with the bank. If you have to correct one, open the account's latest reconciliation report and select **Undo reconciliation**: it reopens with its ticks kept, you untick the transaction, correct it, and finish again.

## More banking tools

These are linked from the Banking page and covered in [Bank connections, checks, positive pay and petty cash](21-banking-tools.md):

- **Print checks** for bill payments and check expenses.
- **Positive pay** files for banks that check your issued checks.
- **Petty cash**: a cash box with a float, counted and topped up.
