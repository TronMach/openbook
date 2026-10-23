using OpenBook.Domain.Companies;

namespace OpenBook.Domain.Accounts;

/// <summary>The federal income tax return a business files, which decides the tax lines its accounts map to.</summary>
public enum TaxForm
{
    /// <summary>Schedule C (Form 1040): sole proprietors and single-member LLCs.</summary>
    ScheduleC,

    /// <summary>Form 1065: partnerships and multi-member LLCs.</summary>
    Form1065,

    /// <summary>Form 1120-S: S corporations.</summary>
    Form1120S,

    /// <summary>Form 1120: C corporations.</summary>
    Form1120,

    /// <summary>Form 990: tax-exempt organizations.</summary>
    Form990,
}

/// <param name="Key">Stable identifier stored on accounts, e.g. "c.8".</param>
/// <param name="Line">The line as printed on the form, e.g. "8" or "16a".</param>
/// <param name="IsIncome">Income lines come first in the tax summary and are shown credit-positive.</param>
public sealed record TaxLine(string Key, TaxForm Form, string Line, string Name, bool IsIncome)
{
    public string Label => Line.Length == 0 ? Name : $"Line {Line}: {Name}";
}

/// <summary>
/// The income and deduction lines of each return that profit and loss accounts can be mapped to, so the tax
/// summary can total them. Line numbers follow the 2025 forms; a changed form keeps its keys and changes labels.
/// </summary>
public static class TaxLines
{
    /// <summary>For expenses the return doesn't allow, such as fines and penalties.</summary>
    public const string NonDeductibleKey = "nondeductible";

    /// <summary>Belongs to no form: <see cref="For"/> offers it on every one.</summary>
    public static readonly TaxLine NonDeductible = new(NonDeductibleKey, TaxForm.ScheduleC, "", "Not deductible", false);

    public static readonly IReadOnlyList<TaxLine> All =
    [
        .. Form(TaxForm.ScheduleC, "c",
            ("1", "Gross receipts or sales", true), ("2", "Returns and allowances", true), ("4", "Cost of goods sold", false),
            ("6", "Other income", true), ("8", "Advertising", false), ("9", "Car and truck expenses", false),
            ("10", "Commissions and fees", false), ("11", "Contract labor", false), ("12", "Depletion", false),
            ("13", "Depreciation", false), ("14", "Employee benefit programs", false), ("15", "Insurance (other than health)", false),
            ("16a", "Mortgage interest", false), ("16b", "Other interest", false), ("17", "Legal and professional services", false),
            ("18", "Office expense", false), ("19", "Pension and profit-sharing plans", false),
            ("20a", "Rent: vehicles, machinery and equipment", false), ("20b", "Rent: other business property", false),
            ("21", "Repairs and maintenance", false), ("22", "Supplies", false), ("23", "Taxes and licenses", false),
            ("24a", "Travel", false), ("24b", "Deductible meals", false), ("25", "Utilities", false), ("26", "Wages", false),
            ("27a", "Other expenses", false)),
        .. Form(TaxForm.Form1065, "p",
            ("1a", "Gross receipts or sales", true), ("1b", "Returns and allowances", true), ("2", "Cost of goods sold", false),
            ("7", "Other income", true), ("9", "Salaries and wages", false), ("10", "Guaranteed payments to partners", false),
            ("11", "Repairs and maintenance", false), ("12", "Bad debts", false), ("13", "Rent", false),
            ("14", "Taxes and licenses", false), ("15", "Interest", false), ("16a", "Depreciation", false),
            ("18", "Retirement plans", false), ("19", "Employee benefit programs", false), ("21", "Other deductions", false)),
        .. Form(TaxForm.Form1120S, "s",
            ("1a", "Gross receipts or sales", true), ("1b", "Returns and allowances", true), ("2", "Cost of goods sold", false),
            ("5", "Other income", true), ("7", "Compensation of officers", false), ("8", "Salaries and wages", false),
            ("9", "Repairs and maintenance", false), ("10", "Bad debts", false), ("11", "Rents", false),
            ("12", "Taxes and licenses", false), ("13", "Interest", false), ("14", "Depreciation", false),
            ("16", "Advertising", false), ("17", "Pension and profit-sharing plans", false), ("18", "Employee benefit programs", false),
            ("20", "Other deductions", false)),
        .. Form(TaxForm.Form1120, "k",
            ("1a", "Gross receipts or sales", true), ("1b", "Returns and allowances", true), ("2", "Cost of goods sold", false),
            ("10", "Other income", true), ("12", "Compensation of officers", false), ("13", "Salaries and wages", false),
            ("14", "Repairs and maintenance", false), ("15", "Bad debts", false), ("16", "Rents", false),
            ("17", "Taxes and licenses", false), ("18", "Interest", false), ("19", "Charitable contributions", false),
            ("20", "Depreciation", false), ("22", "Advertising", false), ("23", "Pension and profit-sharing plans", false),
            ("24", "Employee benefit programs", false), ("26", "Other deductions", false)),
        .. Form(TaxForm.Form990, "n",
            ("VIII 1", "Contributions, gifts and grants", true), ("VIII 2", "Program service revenue", true),
            ("VIII 3", "Investment income", true), ("VIII 11", "Other revenue", true),
            ("IX 5", "Compensation of officers and directors", false), ("IX 7", "Other salaries and wages", false),
            ("IX 11", "Fees for services", false), ("IX 12", "Advertising and promotion", false), ("IX 13", "Office expenses", false),
            ("IX 16", "Occupancy", false), ("IX 17", "Travel", false), ("IX 19", "Conferences and meetings", false),
            ("IX 20", "Interest", false), ("IX 22", "Depreciation", false), ("IX 23", "Insurance", false),
            ("IX 24", "Other expenses", false)),
        NonDeductible,
    ];


    private static readonly Dictionary<string, TaxLine> ByKey = All.ToDictionary(l => l.Key, StringComparer.Ordinal);

    public static TaxLine? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var line) ? line : null;

    /// <summary>The lines of one form in print order, then "Not deductible".</summary>
    public static IReadOnlyList<TaxLine> For(TaxForm form) =>
        [.. All.Where(l => l.Form == form && l.Key != NonDeductibleKey), NonDeductible];

    public static TaxForm FormFor(BusinessType type) => type switch
    {
        BusinessType.MultiMemberLlc or BusinessType.Partnership => TaxForm.Form1065,
        BusinessType.SCorporation => TaxForm.Form1120S,
        BusinessType.CCorporation => TaxForm.Form1120,
        BusinessType.Nonprofit => TaxForm.Form990,
        _ => TaxForm.ScheduleC,
    };

    public static string Describe(TaxForm form) => form switch
    {
        TaxForm.ScheduleC => "Schedule C (Form 1040)",
        TaxForm.Form1065 => "Form 1065",
        TaxForm.Form1120S => "Form 1120-S",
        TaxForm.Form1120 => "Form 1120",
        _ => "Form 990",
    };

    private static IEnumerable<TaxLine> Form(TaxForm form, string prefix, params (string Line, string Name, bool IsIncome)[] lines) =>
        lines.Select(l => new TaxLine($"{prefix}.{l.Line.Replace("VIII ", "8.", StringComparison.Ordinal).Replace("IX ", "9.", StringComparison.Ordinal)}", form, l.Line, l.Name, l.IsIncome));
}
