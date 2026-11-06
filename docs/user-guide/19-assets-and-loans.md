# Fixed assets and loans

Equipment, vehicles and buildings last for years, so their cost is spread over those years as *depreciation*. Loans are repaid in monthly payments, part interest and part the loan itself. OpenBook works both out for you and posts the entries. Open them from **Accounts → Fixed assets** and **Accounts → Loans** (accountant role to change).

## Fixed assets

### Buying the asset

Record the purchase as usual: a bill, an expense or a journal entry, to a **Fixed asset** account such as "Equipment" or "Vehicles". Then add the asset to the register, which posts nothing by itself.

### Adding it to the register

**Fixed assets → Add an asset**:

- **Name** and optional **asset number**.
- **Cost** and the date it was **placed in service**.
- **Asset account**: the fixed asset account the purchase went to.
- **Depreciation** method:
  - **Straight-line**: the same amount every month over the **useful life in years**, down to the **salvage value**.
  - **Declining balance**: a **declining rate %** of what's left each year, so more early on.
  - **MACRS (US tax)**: the IRS tables for the **MACRS class** (3 to 20 years with the half-year convention, 27.5 and 39 years for real estate with mid-month; 39-year buildings use the published percentages of Table A-7a, so the yearly figures match a preparer working from the table). Salvage is ignored, as the IRS does.
  - **Not depreciated (land)**.
- **Accumulated depreciation account** and **depreciation expense account**: leave them on the defaults ("Accumulated Depreciation" and "Depreciation Expense", created when first needed) unless your chart has its own.
- **Depreciation already recorded** **through** a date, for an asset bought before you started with OpenBook. Depreciation carries on from the schedule after that date, and if what you enter differs from the schedule, the last month takes the difference, so the asset still ends at its full depreciable amount.

The asset's page shows its **schedule**, a row a year, adding up exactly to its cost less salvage. Once depreciation has been posted, only the name, number and description can be changed.

### Running depreciation

On **Fixed assets**, under **Run depreciation**, choose **through** a date (usually a month or year end) and **Preview**. It lists what's due for each asset. **Post depreciation** posts one journal entry for all of them: depreciation expense against accumulated depreciation, per asset.

Run it monthly, quarterly or once a year; whatever's due since the last run is posted. If a run was wrong, open it and **Void**: only the latest run can be voided, and its amounts come due again in the next one.

### Selling or disposing of an asset

On the asset's page, **Sell or dispose of it**: the **date**, what it **sold for** (zero if scrapped) and where the **money went** (a bank account, or another current asset account; **Make deposit** doesn't list these, so if it waits in Undeposited Funds, enter a deposit on the bank account with Undeposited Funds as its category). **Dispose**.

OpenBook posts the depreciation due up to that date, takes the asset's cost and accumulated depreciation off the books, records the money received, and puts the difference to "Gain or Loss on Disposal of Assets". **Undo the disposal** reverses it.

## Loans

**Loans → Add a loan** for any debt repaid monthly: a bank loan, an equipment loan, a vehicle loan.

- **Name**, and the **loan account** (a liability account, usually long-term).
- **Amount borrowed** (or still owed, for a loan you're bringing over), **interest rate % a year**, the number of **monthly payments** (left) and the **first payment** date.
- **Payment**: leave it blank to have it worked out so the loan is paid off on time, or enter your lender's figure.
- **Record the money received in** a bank or asset account if the loan is new and the money isn't recorded yet. If it already is (or you're bringing over an existing loan), choose **already recorded**.

### Payments

On the loan's page, **Record a payment**: date, amount and **paid from**. OpenBook splits it: a month's interest on the balance owed goes to "Interest Expense", and the rest pays down the loan account. **What's left** shows the remaining schedule.

Payments must be in date order; only the latest can be voided. Once a payment is recorded, the loan's terms are fixed, though its **name and notes** can still be changed on its page.

A loan entered by mistake: void its payments, latest first, then **Void this loan** on its page, saying why. That reverses the entry recording the money received, and the loan leaves the list, the Loans report and the forecast. To change the terms of a loan, void it and enter it again.

If you also download bank statements, match the payment in the bank review list to the one recorded here rather than adding it again.

## Reports

- **Fixed asset register**: each asset's cost, accumulated depreciation and book value as of a date.
- **Depreciation schedule**: a year's depreciation per asset, by quarter.
- **Loans**: every loan's rate, payment, next payment, interest paid and balance.

Remaining loan payments also feed the cash-flow forecast.
