using OpenBook.Domain.Companies;

namespace OpenBook.Web.Components;

/// <summary>A shortcut on the home page: where it goes and what it says (English; translated where it's shown).</summary>
public sealed record EditionShortcut(string Href, string Label, string Hint);

/// <summary>
/// What each edition changes on screen. Only order and emphasis: a page left out of an edition's menu still works and
/// is linked from elsewhere (Settings lists every area), so switching editions never hides data.
/// </summary>
public static class Editions
{
    public static string Label(Edition edition) => edition switch
    {
        Edition.ProfessionalServices => "Professional services",
        Edition.ECommerce => "E-commerce",
        Edition.Nonprofit => "Nonprofit",
        _ => "General",
    };

    public static string Description(Edition edition) => edition switch
    {
        Edition.ProfessionalServices => "Consultants, agencies, accountants and other firms that bill for their time: projects, time and rates lead.",
        Edition.ECommerce => "Online shops and marketplace sellers: sales, stock and purchasing lead.",
        Edition.Nonprofit => "Charities and other nonprofits: donor restrictions, programs and the nonprofit statements lead.",
        _ => "Every area in the menu, in the usual order.",
    };

    /// <summary>The main menu's links, in order (paths; the layout knows each one's label).</summary>
    public static IReadOnlyList<string> Menu(Edition edition) => edition switch
    {
        Edition.ProfessionalServices => ["/", "/projects", "/time", "/sales", "/purchases", "/banking", "/reports", "/dashboard", "/accounts", "/journal"],
        Edition.ECommerce => ["/", "/sales", "/inventory", "/purchases", "/banking", "/reports", "/dashboard", "/accounts", "/journal"],
        Edition.Nonprofit => ["/", "/nonprofit", "/sales", "/purchases", "/banking", "/reports", "/dashboard", "/accounts", "/journal"],
        _ => ["/", "/accounts", "/banking", "/sales", "/purchases", "/inventory", "/projects", "/journal", "/reports", "/dashboard"],
    };

    /// <summary>Report groups listed before the rest on the reports page.</summary>
    public static IReadOnlyList<string> ReportGroupsFirst(Edition edition) => edition switch
    {
        Edition.ProfessionalServices => ["Projects and time", "Sales and customers"],
        Edition.ECommerce => ["Sales and customers", "Inventory", "Taxes"],
        Edition.Nonprofit => ["Nonprofit", "Financial statements"],
        _ => [],
    };

    public static IReadOnlyList<EditionShortcut> Shortcuts(Edition edition) => edition switch
    {
        Edition.ProfessionalServices =>
        [
            new("/time", "Track time", "Start a timer or enter hours"),
            new("/reports/unbilled-work", "Unbilled work", "Time, mileage and expenses waiting to be invoiced"),
            new("/sales/invoices/new", "New invoice", "Bill a client, with their unbilled work ticked in"),
            new("/reports/utilization", "Utilization", "Billable hours against each person's available hours"),
            new("/time/rates", "Rate card", "Hourly rates by client, person, role or service"),
            new("/projects", "Projects", "Budgets, profitability and what's unbilled per project"),
        ],
        Edition.ECommerce =>
        [
            new("/sales/channels", "Sales channels", "Payouts from Shopify, Stripe, Amazon, Etsy, eBay and PayPal"),
            new("/sales", "Sales", "Invoices, sales receipts and payments"),
            new("/reports/inventory-stock", "Stock status", "What's on hand and what needs reordering"),
            new("/reports/sales-by-item", "Sales by item", "What sells, and for how much"),
            new("/reports/sales-tax", "Sales tax", "What you've charged and owe, by agency"),
        ],
        Edition.Nonprofit =>
        [
            new("/nonprofit", "Donor restrictions", "What each purpose holds, and releasing what's been spent"),
            new("/reports/statement-of-activities", "Statement of activities", "Revenue, support and expenses, without and with donor restrictions"),
            new("/reports/statement-of-financial-position", "Statement of financial position", "Assets, liabilities and net assets"),
            new("/reports/functional-expenses", "Functional expenses", "Expenses by program, management and general, and fundraising"),
            new("/sales/receipts/new", "Record a gift", "A sales receipt to the donor, on a contribution item"),
        ],
        _ => [],
    };
}
