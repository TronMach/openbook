using OpenBook.Domain.Channels;

namespace OpenBook.Web.Components.Channels;

public static class ChannelLabels
{
    public static string Kind(ChannelKind kind) => kind switch
    {
        ChannelKind.Ebay => "eBay",
        ChannelKind.PayPal => "PayPal",
        ChannelKind.Other => "Other (CSV)",
        _ => kind.ToString(),
    };

    /// <summary>Which report to download from the channel, shown beside the import box.</summary>
    public static string ReportHelp(ChannelKind kind) => kind switch
    {
        ChannelKind.Amazon => "Seller Central › Reports › Payments › All statements › download the settlement's flat file (V2).",
        ChannelKind.Ebay => "Seller Hub › Payments › Reports › Transaction report (CSV).",
        ChannelKind.Etsy => "Shop Manager › Finances › Payment account › download the monthly CSV.",
        ChannelKind.PayPal => "Activity › Statements › Activity download, CSV, completed payments. Withdrawals to your bank are left out.",
        ChannelKind.Shopify => "Finances › Payouts › Export transactions (CSV), with the payout ID column. A live connection comes later.",
        ChannelKind.Stripe => "Balance › Payouts › Export, or Reports › Payout reconciliation (CSV) with the transfer column. A live connection comes later.",
        _ => "Any CSV with a date, a type (sale, refund, fee, shipping, tax…) and an amount (or gross and fee) column; a payout id column groups rows into payouts.",
    };

    public static string Status(PayoutStatus status) => status switch
    {
        PayoutStatus.Pending => "Waiting to post",
        PayoutStatus.Posted => "Posted",
        _ => "Dismissed",
    };
}
