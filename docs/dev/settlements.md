# Trucking settlements

Developer notes (index in `CLAUDE.md`). Read this file before changing this area; keep it up to date in the same commit.

## Carriers and settlements (L15, A1, 2026-11-25)

- `Domain/Settlements`: `Carrier` (audit `carrier`, Versioned; name unique ignoring case, unit, deposit account (Bank or Other current asset), optional vendor, class, location and `DueAccountId`; owned `CarrierCode`s keyed by **section and code**, since one code can be a reimbursement and a deduction) and `Settlement` (audit `settlement`, Versioned; number unique per carrier except Dismissed ones, a filtered index; owned `SettlementLine`s and `SettlementDeposit`s, so it loads with `AsSplitQuery`). Waiting (changeable: `ChangeAsync` replaces every line) → Posted (`Source = settlements.settlement`) → void puts it back to Waiting; Dismissed for duplicates. Separate from `ChannelPayout`, whose lines have fixed types, one class per payout, no quantities and tax routing.
- Amounts are signed on lines: positive is money to the owner. `SettlementSections.Counted/Printed` turn a printed amount round in the sections printed positive that take money (Deduction, Wage); Other is printed with its own sign.
- The net has two parts: `NetPaid` (never negative; into `Deposits`, or the carrier's account without any) and `CarriedForward` (negative owed to the carrier, positive held by it), posted to `Carrier.DueAccountId`, "Due to Carrier" found or made when first needed. `Difference` = both less the lines; posting is refused unless it's zero and every line has an account (its own, else its code's). A posted entry needs two lines (`settlement.nothing_to_post`), so a week that nets to zero posts and one that cancels out in one account doesn't.
- Postings are one journal line per account and class, dated the pay date so the bank feed matches it. A line's class is its own, its code's or the carrier's; the deposit and carried-forward lines have none. `PostedAccountId`/`PostedClassId` keep where each line went; `SegmentService.ReclassifyAsync` moves `PostedClassId` with its journal line, so miles follow the class.
- Miles and gallons are on the lines only: never in the journal.
- Line accounts are never A/R, A/P or an item's inventory asset account (`settlement.line_account`): the books check couldn't explain those. What a settlement already has stays usable on it once deactivated.
- `SettlementService.PossibleDepositsAsync`: posted receives of the same amount in the deposit accounts within `BankFeedService.MatchWindowDays` (a bank rule may have added the deposit first). Shown, never refused.
- `SettlementGuess.Account` suggests an account from a code's words and the chart's names (the trucking accounts' and others like them); a person always chooses.
- `SettlementReader.Read(text, reference)`: pasted rows or CSV, headers in any order or `PasteOrder` without one; "0713" dates take the year that puts them within a month after the reference or before it; a file can hold several settlements by number or pay date.
- Audit snapshots list section, code, load and amount, never descriptions: those can name other people.
- Roles: entering, changing and posting are standard; carriers and their codes are accountant (`/settlements/carriers`). Own area `settlements` (`PageAreas`, `ApiScopes`, `RecordAreas`, `SearchAreas`).
- Pages `/settlements`, `/settlements/new`, `/settlements/{id}`, `/settlements/{id}/edit` (one interactive page for both, with `FormDraft`), `/settlements/carriers[/{id}]`; form posts in `SettlementEndpoints`; API `/api/settlements` and `/api/settlements/carriers`.
- Also: books check documents, the to-do list, Close the books, the cash forecast (waiting nets, and last year's), expenses by vendor and the report builder's Name (the carrier's vendor), search, erasing a vendor (an active carrier blocks it).

## Import and review (A2, A3, 2026-11-25)

- `SettlementService.ImportAsync(carrierId, bytes, date, save)`: every settlement of a CSV file waits, or none of them comes in; a number the carrier has is left out; a net below zero becomes carried forward; a file without a net leaves 0 and says so (never the lines' total, which would prove nothing). `Settlement.FileHash` warns about the same file. Form `POST /settlements/import` (one new settlement opens its page), API `POST /api/settlements/import?carrierId=&date=&dryRun=`. Fuzz target `settlement-csv`.
- `SettlementReview` (`SettlementService.ReviewAsync`, `GET /api/settlements/{id}/review`, `SettlementReviewCard`): against up to `Weeks` (3) earlier settlements of the carrier, new codes, codes on every one of them but this, and codes that came to the same amount on at least `RegularAfter` (2) of them until now; lines to a loan's liability account; section totals against the last and `StatedTotals`; account balances against `StatedBalances` (balance sheet accounts, in their own sign). A waiting settlement's own effect is added to the books' balance. Nothing is refused. The form asks again for the balances checked last time.

## The edition and the setting

- `Company.UsesSettlements` puts settlements in the menus whatever the edition (`Editions.Menu(edition, settlements)`, `ViewState.Settlements`); `Edition.Trucking` puts them first. Choosing the edition (setup or Settings) turns the setting on; it can be turned off again. Neither changes data, rules or access.
- `ChartTemplates.AddTruckingAsync` adds `Templates/trucking.csv`'s accounts by name to whatever chart there is (numbers already used are left off; Schedule C lines mapped to the company's form). Not one of `All`, which are whole charts. Offered on Carriers and codes, and applied at setup in the simple view.

Tests: `SettlementTests`, `FormFlowTests.A_carrier_settlement_…`. The sample statement is made up: real ones hold other people's names and pay, so none goes in the repository.
