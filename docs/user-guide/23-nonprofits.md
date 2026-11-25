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
- **Did the donor receive anything in return?** For a gala ticket or a thank-you gift, enter **its fair value** and **what it was**. The deductible amount on the letter is the gift less that value. **Value received goes to** (optional) sends that value to an income account of its own, such as "Special Event Revenue", so a ticket's exchange part is earned revenue and only the rest counts as a contribution; left blank, all of it stays on the contribution account. (It isn't offered on a pledge payment.) Profit and loss by class shows an event's revenue and costs when they share its class.
- **Split this check between purposes** (money gifts that don't pay a pledge): one check for several purposes, say $500 with $200 for scholarships. Enter the whole check as the **Amount**, then each other part with its **Amount**, **Contribution account** and **Purpose (class)**. OpenBook records a gift for each part (same donor, date and check number) and the purpose at the top keeps the rest, with any value received, so it has to keep at least that much. Deposit them from Undeposited Funds together, so the deposit matches the one check the bank sees. Each part is a gift of its own on the donor's statement and can be voided on its own.

- **Campaign or appeal** (optional): the letter, event or drive the gift answered, such as "Year-end letter 2026". Earlier campaigns are suggested as you type. It's kept beside the books, so it can be changed on the gift's page at any time; **Giving by campaign** totals gifts by it.

To correct a gift, use **Change** on its page: donor, amount, date, account, contribution account, purpose, reference, what the donor received and memo (the kind stays). Say **Why are you changing it?** and **Save changes**. OpenBook reverses its journal entry on the original date and posts the corrected one; the gift keeps its number and its history. While it's in a bank deposit or a processor's payout, or matched to a bank feed line, its amount and account stay, and it can't be dated after the deposit. A soft credit above the new amount comes down to it, and one for the new donor is taken off. Taking restricted money off a purpose that's since been released is refused until the release is voided. A gift paying a pledge is voided and recorded again instead. Gifts can also be voided from their page.

A check that bounces after it was deposited: on the gift's page, **Returned by the bank** with **Came back on**, the **Bank's fee** if any and **Why**, then **Record the return**. OpenBook takes the money back out of the bank the deposit went into (and the fee, to "Bank Service Charges") and the gift out of the contribution account on that day, so the deposit stays as it happened and the bank reconciles. The gift is marked **Returned by the bank** and no longer counts on the donor's page, statements or letters, nor waits to be thanked; a returned pledge payment leaves the pledge owed again. If the check clears after all, **Take the return back**. A gift still in Undeposited Funds is voided instead, and one that came in through a processor's payout is handled at the processor.

### Many gifts at once

**Enter a batch** (on the Gifts page) is for a day's mail or an event. Set the date, where the money went and, if you like, the purpose and campaign, and type in the **Batch total** from your deposit slip; then add a row per gift. Someone new? Type the name in the donor box and choose **+ Add**. The form shows how far the gifts are from the total, and **Record** saves all of them or none.

**Import gifts** reads a CSV with **Date**, **Donor** and **Amount** columns, and uses a check or reference number, a memo, a purpose (a class, by its full name) and a **Campaign** (or **Appeal**) when they're there. It shows what it found first. Donors who aren't on your list are added, a gift already in the books with the same donor, date, amount and reference is skipped, and nothing comes in unless every row is good.

### Seeing what a donor has given

A donor's page has a **Giving** card once there's anything to show: this year, last year, all time, the last gift, what's still to come on pledges and any grants, their **Soft credits** and their **Household**. **Their gifts** opens the Gifts page for that donor alone. Their gifts, pledges and grants also list in the **Customer Center** and turn up in search, by donor, reference or amount.

### Households

A couple or a family who give together can get one year-end statement instead of one each. **Nonprofit → Households** (standard role): under **New household** choose the **First member**, who the statement goes to (their address and email), and optionally a **Name** ("The Smith household"; blank uses the member's name) and who **Statements addressed to** ("Pat and Sam Smith"). On the household's page, **Add a member**, **Send statements here** to send them to another member, **Take out** a member, rename it, or **Remove household**.

A donor is in one household at most. Each member keeps their own gifts, acknowledgment letters and history; a household changes nothing in the books. Its statement lists every member's gifts for the year, each under the giver's name. Merging two donors keeps one membership; a household whose primary member went with it sends statements to a member it still has, and is removed when it has none.

### Soft credits

A soft credit recognizes someone besides the donor for a gift: a spouse, or a board member who brought the gift in. On the gift's page, under **Soft credits**, choose who to **Credit**, an **Amount** (blank: all of the gift) and **Why**, then **Add soft credit**; **Take off** removes one. Soft credits show on the person's Giving card and on **Giving by donor**, **Giving by household** and **Soft credits** reports, never on acknowledgment letters or giving statements, which belong to the person who paid. They post nothing.

### Gifts made online

**Your own giving page.** With online payments set up (**Settings → Online payments**, a Stripe account), tick **Open the giving page** under **Nonprofit → Settings → Online giving**. Donors then give at `/give` on your public address: they type the amount, their name and email and an optional note, and pay on Stripe's page. When Stripe reports the payment, OpenBook records the gift for the donor with that email (adding a new donor when there's none), into the online payments' deposit account with the campaign **Online giving**. Without a webhook, the gift is recorded when the donor comes back to the thank-you page, or when OpenBook next asks the provider (every ten minutes for the first hour, then hourly for a day). Gifts started and not recorded are listed on the **Gifts** page under **Online gifts not recorded yet**, each with **Check if paid**. Refunds and disputes of gifts aren't reported: watch the provider's dashboard for those. Import the Stripe payout as usual: it finds those gifts by their charge and clears them, so nothing counts twice.

Gifts through Stripe, PayPal or a giving platform reach you as a payout, after fees. To get a record of each donor as well:

1. Add the processor as a sales channel ([chapter 22](22-sales-channels.md)).
2. **Import gifts** from the processor's export of payments, into **Undeposited Funds**. Its amount (gross) goes in as the gift, and its charge or transaction id as the reference.
3. Import the payout report and post it. A charge with a gift's reference clears that gift rather than counting as a sale, the fee goes to fees, and the net reaches the bank.

Gifts cleared this way don't wait to be deposited, and can't be voided until the payout is. Thank-you letters and giving statements work as usual.

### Spreading shared costs

**Nonprofit → Shared cost allocations** spreads an expense such as rent across programs and functions. A rule names the account, where the cost sits until it's spread (lines with no class, or a class you keep for it) and each class's percentage, adding up to 100. Pick a period, check what's in each pool, and **Post the allocations**: one entry per rule, dated at the end of the period, moving the cost out of the pool. One account and pool takes one active rule, since a rule moves all of it. Running it again moves only what's new, and a run can be voided. Tick **Run at each month end by itself** on a rule and OpenBook runs it for the month just ended, once, early in the next month, so rent entered on bills or recurring templates is spread without a manual run. If a run is refused (the month is closed, say), **Settings → System** shows the job's error.

### Recurring gifts

**Nonprofit → Recurring gifts** for monthly card gifts, standing transfers or pledges paid in installments: donor, amount, deposited to, **every** so often, **starting** and **ending** (or after a number of gifts). Each becomes an ordinary gift on its date, automatically within the hour, or by hand with **Record what's due**. **Stop** ends one. On a pledge, the last gift is whatever's left of it, and the recurring gift then stops by itself.

A recurring gift is recorded on its date whether or not the money came: OpenBook can't see the donor's card or bank. Check each one against the bank (reconciling does this), and void any that didn't arrive, or the books and the donor's statement will show money you never received.

## Thanking donors

**Nonprofit → Thank donors**:

- **Gifts not thanked yet**: tick gifts and **Email the ticked gifts' letters** (each donor with an email address gets theirs, and those gifts are marked thanked), **Letters for the ticked gifts (one PDF)** to print them together, or **Mark the ticked gifts as thanked** after printing or sending them another way. A single gift's letter is also on its page.
- **Year-end giving statements**: choose the **year** and **Show**. Download each donor's statement, all of them in one PDF, or **email every donor with an address**. A household's members who gave share one row and one statement, sent to the member it goes to.

Letters use your company name, address and form style. **Nonprofit → Settings** (letter settings) adds:

- **EIN**: with it, letters say the organization is tax-exempt under section 501(c)(3).
- **Signed by** and **title**.
- **Opening paragraph**, or leave it empty for a plain thank-you.

Letters include the IRS wording: what was received in return and its value, or that no goods or services were provided, and for in-kind gifts, a description without a value.

## Pledges

A *pledge* is a promise to give. On accrual basis it's income when made; on cash basis, when paid.

**Nonprofit → Pledges → Record a pledge**: donor, amount, date, **due by**, contribution account and purpose. It's paid by gifts that name it (**Paying a pledge** on the gift form). The pledge page lists its payments and what's left.

- **Installments**: a pledge paid over time can have a schedule: how many installments, how far apart (a month, 3 or 6 months, a year) and the first due date make equal amounts, the last taking any cents left over. **Replace the schedule** or **Clear the schedule** at any time; it posts nothing. **Pledge statement (PDF)** on the pledge's page is a letter for the donor: what they promised, each payment so far, what's left and what's due. **Email a reminder** on a pledge with money left opens an email to the donor saying what's received, what's due now and when the next payment is due, with the statement attached; its wording is under **Settings → Email templates** (**Pledge reminder**). **Reports → Open pledges** shows each pledge's next due date and what's **Overdue**: what the schedule (or the due date) expected by the report's date, less what's been paid.
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

- **Budget and spending**: enter a budget by account (**Save budget**) and compare spending against it. When the funder budgets by its own categories ("Personnel", "Travel"), give each line a **Funder's category**: several accounts can share one, and an account with a category but no amount puts its spending in that category. The page then shows the categories first, each with its accounts, and **Grant budget vs. actual** groups its lines under them with a total for each; lines without a category come last, under **Not in a category**.
- **Money from the funder**: record each payment (**Record payment**), into a bank account or into Undeposited Funds to go into a deposit with other checks (**Banking → Make deposit** lists it; voiding the deposit puts it back, and a deposited payment can't be voided until then). On a conditional grant, money received before it's earned is held as a refundable advance until spending catches up.
- **Change the grant**: correct its name, award number, period or terms, or **Amend the award** when the funder adds money (an unconditional grant's extra is income and receivable on the date; a conditional one can then recognize more).
- **Matching grants** (conditional only): under **Change the grant**, **Funder pays (% of spending)** says what share of the spending on the grant's class the funder covers. Recognition then counts only that share as grant income, and the grant page shows your match so far. 100% unless you change it.
- **Federal awards**: under **Change the grant**, give the **Assistance Listing number** (such as 93.870), the **Federal agency** and, for federal money passed through a state or another organization, who **Passed it through**. **Reports → Schedule of expenditures of federal awards** then lists each federal award's expenditures for the period by agency: the funder's share of the spending on its class, with its indirect costs. On a conditional grant, **Indirect rate (%)** (a negotiated or de minimis rate) is earned on top of that spending, in what's recognized and in reimbursement requests. Spending beyond the award isn't listed. Effort reporting isn't done.
- **When the funder won't pay it all**: **Reduce the award** by what won't come, with the reason. On an unconditional grant this takes that income and the receivable back out on the date; on a conditional one it posts nothing and caps what can still be recognized. **Return the advance** pays back money received ahead and not spent: out of the bank, off the refundable advance. Both show under payments and income recognized, and can be voided.
- **Reports due to the funder**: add each report the grant asks for with its due date (**Add a report due**) and **Mark sent** when it goes. Reports due within two weeks or late, grants ending within 30 days, and conditional grants with spending not yet recognized are on the home page's to-do list. A report not marked sent is also emailed to the admins a week before it's due, on the day and each week while it's late, or as soon after as OpenBook is running (the same alerts as for failed backups: chapter 8).
- **Close the grant** when it ends (**Reopen** if needed), or void it; void individual payments and recognitions.

### Reporting to a funder

On a grant's page, **Grant budget vs. actual** is the report to send: budget, this period, to date and what's left, by budget line. Under **Reports**, choose **Every grant of the period** as its grant for one report with a section and total per grant (every grant whose dates meet the period), for the board or a funder with several grants. **Make a request** writes a reimbursement request for a period, with the spending by line and the amount, as a letter to print or send. Left blank, the amount is what's spent, up to the award as amended, less what's paid or asked for already. It posts nothing. When the money comes, choose the request under **Pays request** as you record the payment.

## Reports

In the **Nonprofit** group:

- **Statement of activities**: revenue, support and expenses, without and with donor restrictions, and the net assets they leave.
- **Statement of financial position**: the balance sheet with net assets split into without and with donor restrictions. Money the board has set aside (a reserve, a quasi-endowment) shows on its own line when it's in an equity account whose name starts "Board-designated"; the nonprofit starter chart has **Board-Designated Net Assets**, **Endowment Net Assets** (donor restricted), **Investments** and **Investment Return**. Record the month's market change with a journal entry, and reconcile the investment account to the broker's statement with **Reconcile to a statement** on its register.
- **Statement of functional expenses**: expenses by account and function, with a column per program.
- **Restricted net assets by purpose**: what each purpose holds.
- **Net asset roll-forward** (net assets without and with donor restrictions from the start of a period to its end, with releases between them) and **In-kind gifts** (what was given and its recorded value), for the audit and Schedule M; the open pledges and grants reports are the pledges and grants schedules.
- **Endowment roll-forward**, for the endowment note in your financial statements: the endowment funds' net assets at the start, **Contributions**, **Investment return** (income, gains and losses), what was **Appropriated for expenditure**, other changes such as releases, and the end, without and with donor restrictions. An endowment fund is a class with "Endowment" in its name (and the classes under it), so post its gifts, investment return and spending on that class. An endowment brought in with the opening balances counts when its equity line is on that class. Investment return is what's on accounts named for investments, interest, dividends or gains, or mapped to Form 990 Part VIII line 3 or 7, less fees and losses on expense accounts named for investments.
- **Schedule B contributors**: donors whose gifts in a calendar year came to $5,000 or more, money and in kind, with their addresses (the general rule of Form 990 Schedule B).
- **Giving by donor** (with each donor's **Soft credits** in a column of their own, not in the total), **Giving by household** (members' gifts added up together; soft credits between members of one household aren't counted twice), **Soft credits** (each credit with its gift and why), **Giving by campaign**, **Lapsed donors** (gave last fiscal year but not yet this one; with a fiscal year starting in July, "2026" is July 2026 to June 2027) and **Open pledges**.
- **Grants**: awards, spending, recognized income and what funders still owe.
- **Form 990 Part IX expenses**: expenses by 990 line and function, from your accounts' tax lines.

Each grant needs its own class or subclass: two grants on one class in the same dates are refused, because both would count the same spending; extending a grant into another's dates is refused too. A gift, pledge or grant whose restricted money has been released can't be voided or reduced until that release is (a pledge's payment can: the pledge is only owed again).

Three statements can sit beside an earlier period: **Statement of activities**, **Statement of financial position** and **Statement of functional expenses** each have a comparison choice, and activities can be run for one class or location. The accountant's package includes the nonprofit statements for a nonprofit company.

**Budget by program** puts each class's budget side by side for the board: year to date against budget, then the total, the year's budget and what's left. It uses budgets made for a class for the fiscal year of the date you choose.
