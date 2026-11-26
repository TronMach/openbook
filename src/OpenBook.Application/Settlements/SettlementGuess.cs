using OpenBook.Domain.Accounts;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

/// <summary>
/// Which account a statement's words suggest for a code, from the accounts the company has (the trucking starter chart's
/// names, and others like them). Only a suggestion: a person chooses, once, and the carrier's code remembers it.
/// </summary>
public static class SettlementGuess
{
    // The first row whose words are in the statement's text, then the first of its account names the chart has an account
    // like. More particular words come first ("fuel tax" before "fuel").
    private static readonly (string[] Words, string[] Accounts)[] Rows =
    [
        (["ADVANCE", "PAYBACK", "PAY BACK", "LOAN"], ["carrier advances"]),
        (["FUEL SURC", "SURCHARGE", "FSC"], ["fuel surcharge"]),
        (["FUEL TAX", "IFTA"], ["fuel tax"]),
        (["TRACTOR DEF", "DEF DISC", "DEF GAL", "EXHAUST FLUID"], ["diesel exhaust fluid", "fuel"]),
        (["FUEL CARD", "WIRE", "BANK CHARGE"], ["bank fees", "bank charges"]),
        (["FUEL", "DIESEL"], ["fuel"]),
        (["TIRE RESERVE", "TIRE FUND", "TIRE ACCRUAL"], ["tire reserve"]),
        (["TIRE"], ["tires", "repairs"]),
        (["EMERGENCY", "EMER FUND"], ["emergency fund"]),
        (["PERFORM", "BOND", "ESCROW"], ["performance bond", "escrow"]),
        (["TOLL", "EZ PASS", "EZPASS", "E-ZPASS", "EZ FAST"], ["toll reimbursements", "tolls"]),
        (["TARP"], ["tarp", "accessorial"]),
        (["DETENTION", "LAYOVER", "STOP OFF", "PICKUP"], ["accessorial", "detention"]),
        (["TRIP REVENUE", "LINEHAUL", "LINE HAUL", "FREIGHT", "LOAD PAY"], ["freight revenue", "freight"]),
        (["TRAINING"], ["training"]),
        (["SCALE", "WEIGH", "PREPASS"], ["scales", "tolls"]),
        (["HWY TAX", "HIGHWAY", "2290", "HEAVY VEHICLE"], ["highway use tax", "taxes and licenses"]),
        (["LICENSE", "PERMIT", "PLATE", "REGISTRATION"], ["licenses and permits", "taxes and licenses"]),
        (["WORK COMP", "WORKERS COMP", "W/C"], ["workers' compensation", "insurance"]),
        (["INSUR", "OCCUP ACC", "CARGO", "BOBTAIL", "PHYSICAL DAMAGE", "LIABILITY"], ["insurance"]),
        (["F.I.C.A", "FICA", "F.U.T.A", "FUTA", "S.U.T.A", "SUTA", "UNEMPLOYMENT"], ["payroll taxes"]),
        (["WAGE", "SEAT PAY", "CO-DRIVER", "DRIVER PAY"], ["driver wages", "wages", "contract labor"]),
        (["TRUCK PAYMENT", "TRUCK RENT", "TRUCK LEASE", "RENTAL", "LEASE", "APU"], ["truck rent", "truck lease", "rent"]),
        (["MILEAGE CHARGE"], ["mileage charges", "truck rent"]),
        (["REPAIR", "MAINT", "SHOP", "PARTS", "INV#", "INV "], ["repairs"]),
        (["ACCOUNTING", "BOOKKEEPING", "STATEMENT PREP"], ["accounting", "professional fees"]),
        (["PAYROLL FEE", "PAYROLL CHG", "IMAGE", "ADMIN FEE", "SETTLEMENT FEE"], ["carrier fees", "commissions and fees"]),
        (["INTEREST"], ["interest income", "interest"]),
        (["BALANCE FORWARD", "BALANCE FWD", "PRIOR BALANCE"], ["due to carrier"]),
    ];

    public static Guid? Account(ChartOfAccounts chart, SettlementSection section, string text)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(text);
        var upper = $" {text.ToUpperInvariant()} ";
        var row = Rows.FirstOrDefault(r => r.Words.Any(w => upper.Contains(w, StringComparison.Ordinal)));
        if (row.Accounts is null)
            return null;
        var income = section is SettlementSection.Revenue;
        var open = chart.Ordered(includeInactive: false).Select(x => x.Account)
            .Where(a => a.Type is not (AccountType.AccountsReceivable or AccountType.AccountsPayable or AccountType.Bank or AccountType.CreditCard or AccountType.Equity)).ToList();
        foreach (var name in row.Accounts)
        {
            // Pay goes to income and what's taken doesn't; a reserve's account is right from either side.
            var found = open.Where(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .Where(a => a.Type.IsBalanceSheet() || income == IsIncome(a.Type) || section is SettlementSection.Other)
                .OrderBy(a => a.Name.Length).FirstOrDefault();
            if (found is not null)
                return found.Id;
        }

        return null;
    }

    private static bool IsIncome(AccountType type) => type is AccountType.Income or AccountType.OtherIncome;
}
