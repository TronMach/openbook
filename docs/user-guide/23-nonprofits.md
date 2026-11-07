# Nonprofits: restrictions, gifts, pledges and grants

OpenBook keeps a nonprofit's books the way the nonprofit statements need them: net assets without and with donor restrictions, expenses by function, gifts with acknowledgment letters, pledges and grants. Choose the **Nonprofit** edition at setup (or in Settings) and the **Nonprofit** starter chart, and donors, gifts and the nonprofit statements come first.

Donors are customer records, called **Donors** in this edition. Most pages here need the accountant role to change; recording gifts and pledges and thanking donors need only the standard role.

## Donor restrictions

A restricted gift may only be spent on its *purpose* (a program, a project, a capital campaign) or after a date. OpenBook tracks restrictions with two things you already have:

- **Accounts**: tick **Holds amounts with donor restrictions** on the income and net asset accounts that hold restricted money, for example "Contributions:Restricted". The nonprofit starter chart has these set up.
- **Classes**: the class on a restricted gift is its purpose.

So a restricted gift is income on a restricted account, with the purpose as its class. Everything on restricted accounts counts as net assets *with* donor restrictions; everything else is *without*.

### Releasing restrictions

When you spend money on a purpose, the restriction is met and that much moves to unrestricted. Spending on a subclass counts for the nearest class above it that holds restricted money. **Nonprofit** (`/nonprofit`) lists each purpose, **as of** a date: **received**, **released**, **still restricted**, **spent on it**, and a suggested release (what's been spent but not yet released). Enter an amount and **Release**.

A release posts between "Released From Restricted" and "Released To Unrestricted" on the purpose's class. Total net assets don't change. You can't release more than the purpose holds. Void a release from its page.

## Functional expenses

Nonprofits report expenses by function: **program services**, **management and general**, and **fundraising**. Each class can carry its functional area (on the class's page); subclasses take their parent's.

On the Nonprofit page, **Set up the usual classes** adds "Programs", "Management and General" and "Fundraising" with their areas, and turns class tracking on. Add each program as a class under "Programs", then put a class on every expense line.

### One class tree for programs, functions, grants and purposes

OpenBook keeps all of these on the line's class, so plan the tree once: functions at the top ("Programs", "Management and General", "Fundraising"), each program under "Programs", and a grant or a donor-restricted purpose under the program it pays for. Then a line on "Programs:Food pantry:USDA grant" counts toward the grant, the food pantry's column and program services all at once, and restricted money for the pantry is released by what's spent there. What the tree can't hold is one grant paying for two programs: give it a class of its own at the top, or split the costs to each program's own grant class. Fixed assets and loans take a class too, so depreciation and interest land in the right column.

## Gifts

**Nonprofit → Record a gift** (`/nonprofit/gifts/new`):

- **Donor**, **amount** and **date**.
- **Kind**: **Money** (cash, check, card, transfer) or **In kind** (goods or services). For in-kind gifts, describe **what was given**; the amount is its fair value, recorded to an asset or expense account.
- **Deposited to**: a bank account, or Undeposited Funds to include it in a deposit later.
- **Contribution account** (default "Contributions", or a restricted account) and **purpose (class)**.
- **Paying a pledge**: choose the pledge, and the gift takes its account and purpose.
- **Did the donor receive anything in return?** For a gala ticket or a thank-you gift, enter **its fair value** and **what it was**. The deductible amount on the letter is the gift less that value.

- **Campaign or appeal** (optional): the letter, event or drive the gift answered, such as "Year-end letter 2026". Earlier campaigns are suggested as you type. It's kept beside the books, so it can be changed on the gift's page at any time; **Giving by campaign** totals gifts by it.

Gifts can be voided from their page.

### Many gifts at once

**Enter a batch** (on the Gifts page) is for a day's mail or an event. Set the date, where the money went and, if you like, the purpose and campaign, and type in the **Batch total** from your deposit slip; then add a row per gift. Someone new? Type the name in the donor box and choose **+ Add**. The form shows how far the gifts are from the total, and **Record** saves all of them or none.

**Import gifts** reads a CSV with **Date**, **Donor** and **Amount** columns, and uses a check or reference number, a memo, a purpose (a class, by its full name) and a **Campaign** (or **Appeal**) when they're there. It shows what it found first. Donors who aren't on your list are added, a gift already in the books with the same donor, date, amount and reference is skipped, and nothing comes in unless every row is good.

### Seeing what a donor has given

A donor's page has a **Giving** card once there's anything to show: this year, last year, all time, the last gift, what's still to come on pledges and any grants. **Their gifts** opens the Gifts page for that donor alone. Their gifts, pledges and grants also list in the **Customer Center** and turn up in search, by donor, reference or amount.

### Gifts made online

Gifts through Stripe, PayPal or a giving platform reach you as a payout, after fees. To get a record of each donor as well:

1. Add the processor as a sales channel ([chapter 22](22-sales-channels.md)).
2. **Import gifts** from the processor's export of payments, into **Undeposited Funds**. Its amount (gross) goes in as the gift, and its charge or transaction id as the reference.
3. Import the payout report and post it. A charge with a gift's reference clears that gift rather than counting as a sale, the fee goes to fees, and the net reaches the bank.

Gifts cleared this way don't wait to be deposited, and can't be voided until the payout is. Thank-you letters and giving statements work as usual.

### Spreading shared costs

**Nonprofit → Shared cost allocations** spreads an expense such as rent across programs and functions. A rule names the account, where the cost sits until it's spread (lines with no class, or a class you keep for it) and each class's percentage, adding up to 100. Pick a period, check what's in each pool, and **Post the allocations**: one entry per rule, dated at the end of the period, moving the cost out of the pool. Running it again moves only what's new, and a run can be voided.

### Recurring gifts

**Nonprofit → Recurring gifts** for monthly card gifts, standing transfers or pledges paid in installments: donor, amount, deposited to, **every** so often, **starting** and **ending** (or after a number of gifts). Each becomes an ordinary gift on its date, automatically within the hour, or by hand with **Record what's due**. **Stop** ends one. On a pledge, the last gift is whatever's left of it, and the recurring gift then stops by itself.

A recurring gift is recorded on its date whether or not the money came: OpenBook can't see the donor's card or bank. Check each one against the bank (reconciling does this), and void any that didn't arrive, or the books and the donor's statement will show money you never received.

## Thanking donors

**Nonprofit → Thank donors**:

- **Gifts not thanked yet**: tick gifts and **Email the ticked gifts' letters** (each donor with an email address gets theirs, and those gifts are marked thanked), **Letters for the ticked gifts (one PDF)** to print them together, or **Mark the ticked gifts as thanked** after printing or sending them another way. A single gift's letter is also on its page.
- **Year-end giving statements**: choose the **year** and **Show**. Download each donor's statement, all of them in one PDF, or **email every donor with an address**.

Letters use your company name, address and form style. **Nonprofit → Settings** (letter settings) adds:

- **EIN**: with it, letters say the organization is tax-exempt under section 501(c)(3).
- **Signed by** and **title**.
- **Opening paragraph**, or leave it empty for a plain thank-you.

Letters include the IRS wording: what was received in return and its value, or that no goods or services were provided, and for in-kind gifts, a description without a value.

## Pledges

A *pledge* is a promise to give. On accrual basis it's income when made; on cash basis, when paid.

**Nonprofit → Pledges → Record a pledge**: donor, amount, date, **due by**, contribution account and purpose. It's paid by gifts that name it (**Paying a pledge** on the gift form). The pledge page lists its payments and what's left.

- **Cancel the rest** when the donor won't pay what's left: it comes off contribution income and pledges receivable. **Undo the cancellation** if they pay after all.
- **Void** only a pledge recorded by mistake or withdrawn before anything was paid.

## Grants

**Nonprofit → Grants → Add a grant**:

- **Name**, **funder** (a customer record), **award number**, **amount awarded**, **awarded on**, and the **grant period** (starts, ends).
- **Class**: expenses on this class (and its subclasses) during the grant period count against the grant. Leave it empty to create a class named after the grant, at the top as a program of its own, or under the program it pays for with **New class goes under**.
- **Income account**: most grants are for a purpose or period, so choose a restricted account.
- **Conditional**: tick for grants that are income only as the condition is met, such as cost reimbursement or matching grants.
- **Terms**: allowed costs, reports due, conditions, for your own reference.

An unconditional grant is recorded as income and receivable when awarded. A conditional one records nothing until it's earned: on its page, **Recognize income earned** posts what's been spent so far (up to the award) as grant income.

On the grant's page:

- **Budget and spending**: enter a budget by account (**Save budget**) and compare spending against it.
- **Money from the funder**: record each payment (**Record payment**). On a conditional grant, money received before it's earned is held as a refundable advance until spending catches up.
- **Change the grant**: correct its name, award number, period or terms, or **Amend the award** when the funder adds money (an unconditional grant's extra is income and receivable on the date; a conditional one can then recognize more).
- **When the funder won't pay it all**: **Reduce the award** by what won't come, with the reason. On an unconditional grant this takes that income and the receivable back out on the date; on a conditional one it posts nothing and caps what can still be recognized. **Return the advance** pays back money received ahead and not spent: out of the bank, off the refundable advance. Both show under payments and income recognized, and can be voided.
- **Reports due to the funder**: add each report the grant asks for with its due date (**Add a report due**) and **Mark sent** when it goes. Reports due within two weeks or late, grants ending within 30 days, and conditional grants with spending not yet recognized are on the home page's to-do list.
- **Close the grant** when it ends (**Reopen** if needed), or void it; void individual payments and recognitions.

### Reporting to a funder

On a grant's page, **Grant budget vs. actual** is the report to send: budget, this period, to date and what's left, by budget line. **Make a request** writes a reimbursement request for a period, with the spending by line and the amount, as a letter to print or send. It posts nothing. When the money comes, choose the request under **Pays request** as you record the payment.

## Reports

In the **Nonprofit** group:

- **Statement of activities**: revenue, support and expenses, without and with donor restrictions, and the net assets they leave.
- **Statement of financial position**: the balance sheet with net assets split into without and with donor restrictions. Money the board has set aside (a reserve, a quasi-endowment) shows on its own line when it's in an equity account whose name starts "Board-designated"; the nonprofit starter chart has **Board-Designated Net Assets**, **Endowment Net Assets** (donor restricted), **Investments** and **Investment Return**. Record the month's market change with a journal entry, and reconcile the investment account to the broker's statement with **Reconcile to a statement** on its register.
- **Statement of functional expenses**: expenses by account and function, with a column per program.
- **Restricted net assets by purpose**: what each purpose holds.
- **Giving by donor**, **Giving by campaign**, **Lapsed donors** (gave last fiscal year but not yet this one; with a fiscal year starting in July, "2026" is July 2026 to June 2027) and **Open pledges**.
- **Grants**: awards, spending, recognized income and what funders still owe.
- **Form 990 Part IX expenses**: expenses by 990 line and function, from your accounts' tax lines.

Each grant needs its own class or subclass: two grants on one class in the same dates are refused, because both would count the same spending. A gift or pledge whose restricted money has been released can't be voided until that release is.

Three statements can sit beside an earlier period: **Statement of activities**, **Statement of financial position** and **Statement of functional expenses** each have a comparison choice, and activities can be run for one class or location. The accountant's package includes the nonprofit statements for a nonprofit company.

**Budget by program** puts each class's budget side by side for the board: year to date against budget, then the total, the year's budget and what's left. It uses budgets made for a class for the fiscal year of the date you choose.
