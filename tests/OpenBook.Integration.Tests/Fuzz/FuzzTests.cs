using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenBook.Application.Banking;
using OpenBook.Application.Channels;
using OpenBook.Application.Documents;
using OpenBook.Application.Import;
using OpenBook.Application.Import.Migration;
using OpenBook.Application.Reporting;
using OpenBook.Application.Updates;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;
using OpenBook.Domain.Documents;
using OpenBook.Domain.Email;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Purchases;
using OpenBook.Infrastructure.Documents;

namespace OpenBook.Integration.Tests.Fuzz;

/// <summary>
/// Fuzzing the readers of files and text people hand OpenBook (imports, statements, channel reports, documents, typed
/// lists, answers from services): each gets its valid samples damaged thousands of ways (<see cref="Mutator"/>) and must
/// either read the result or refuse it with a <see cref="DomainException"/> (the message the page shows), never fail
/// with anything else, which would be an error page, and never take more than <see cref="Limit"/>. No database.
/// Deterministic: the same reader and case number damage the same way, so a failure names the case to rerun. Set
/// OPENBOOK_FUZZ_CASES for a longer run (the default keeps the suite quick); failing inputs are saved under
/// artifacts/fuzz to look at.
/// </summary>
public sealed class FuzzTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private static int Cases => int.TryParse(Environment.GetEnvironmentVariable("OPENBOOK_FUZZ_CASES"), out var n) && n > 0 ? n : 300;

    private sealed record Target(string Name, Action<byte[]> Read, params string[] Seeds)
    {
        /// <summary>Samples that aren't text (fonts).</summary>
        public byte[][] Binary { get; init; } = [];
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static readonly Target[] Targets =
    [
        new("iif", b => MigrationReaders.Read(MigrationSource.QuickBooksIif, b, "USD"), Seeds.Iif),
        new("qbxml", b => { MigrationReaders.Read(MigrationSource.QuickBooksQbxml, b, "USD"); MigrationReaders.ReadCompany(MigrationSource.QuickBooksQbxml, b); }, Seeds.Qbxml),
        new("quickbooks-online", b => { MigrationReaders.Read(MigrationSource.QuickBooksOnline, b, "USD"); MigrationReaders.ReadCompany(MigrationSource.QuickBooksOnline, b); }, Seeds.QuickBooksOnline),
        new("gnucash", b => { MigrationReaders.Read(MigrationSource.GnuCash, b, "USD"); MigrationReaders.ReadCompany(MigrationSource.GnuCash, b); }, Seeds.GnuCash),
        new("xero", b => MigrationReaders.Read(MigrationSource.Xero, b, "USD"), Seeds.XeroChart, Seeds.XeroContacts, Seeds.XeroJournal),
        new("wave", b => MigrationReaders.Read(MigrationSource.Wave, b, "USD"), Seeds.Wave),
        new("freshbooks", b => MigrationReaders.Read(MigrationSource.FreshBooks, b, "USD"), Seeds.FreshBooks),
        new("ofx", b => Ofx.Parse(Text(b)), Seeds.SgmlOfx, Seeds.XmlOfx),
        new("csv", b => Csv.Parse(Text(b)), Seeds.Statement, Seeds.Wave, Seeds.Etsy),
        new("statement-values", b => StatementLine(Text(b)), Seeds.Statement),
        new("settlement-csv", b => OpenBook.Application.Settlements.SettlementReader.Read(Text(b), new DateOnly(2026, 3, 13)), Seeds.Settlement),
        new("owner-charges-csv", b => OpenBook.Application.Settlements.OwnerChargeReader.Read(Text(b)), Seeds.FuelCard, Seeds.Tolls),
        new("settlement-words", b => OpenBook.Application.Settlements.SettlementTextParser.Parse(
            new OpenBook.Application.Settlements.StatementWords(true, StatementPages.ReadTsv(Text(b), 1)), new DateOnly(2026, 3, 13)), Seeds.OcrWords),
        new("settlement-answer", b => ClaudeSettlementReader.Parse(Text(b)), Seeds.SettlementAnswer),
        new("settlement-pdf", b =>
        {
            var pages = new StatementPages(null);
            var count = pages.Inspect("a.pdf", b).Count;
            if (count > 0)
                pages.WordsAsync("a.pdf", pages.Extract("a.pdf", b, [1]), [1], default).GetAwaiter().GetResult();
        }) { Binary = [FakeStatementPages.Pdf("REVENUE SECTION\nRV 100 1 0309 TRIP 1.00", null)] },
        new("channel-amazon", b => ChannelReports.Read(ChannelKind.Amazon, Text(b)), Seeds.Amazon),
        new("channel-ebay", b => ChannelReports.Read(ChannelKind.Ebay, Text(b)), Seeds.Ebay),
        new("channel-etsy", b => ChannelReports.Read(ChannelKind.Etsy, Text(b)), Seeds.Etsy),
        new("channel-paypal", b => ChannelReports.Read(ChannelKind.PayPal, Text(b)), Seeds.PayPal),
        new("channel-general", b => ChannelReports.Read(ChannelKind.Shopify, Text(b), "payouts.csv"), Seeds.ShopifyPayouts),
        new("channel-orders", b => { ChannelOrderReports.Read(ChannelKind.Amazon, Text(b)); ChannelOrderReports.Read(ChannelKind.Etsy, Text(b)); ChannelOrderReports.Read(ChannelKind.Shopify, Text(b)); },
            Seeds.AmazonOrders, Seeds.EtsyOrders, Seeds.ShopifyOrders),
        new("channel-returns", b => ChannelOrderReports.ReadReturns(Text(b)), Seeds.Returns),
        new("document-text", b => { foreach (var kind in Enum.GetValues<DocumentKind>()) DocumentParser.Parse(Text(b), kind, ["Paper Co", "Acme"], "Browser Test Co"); }, Seeds.VendorInvoice),
        new("receipt-text", b => ReceiptParser.Parse(Text(b), ["Corner Cafe", "Paper Co"]), Seeds.Receipt),
        new("claude-answer", b => ClaudeDocumentReader.Parse(Text(b)), Seeds.ClaudeAnswer),
        new("simplefin", b => SimpleFin.Parse(Text(b), TimeZoneInfo.Utc), Seeds.SimpleFin),
        new("passkey", b =>
        {
            var parts = Text(b).Split('|');
            OpenBook.Application.Companies.WebAuthn.Register(new OpenBook.Application.Companies.NewPasskey(parts[0], parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2), -7), "c", "https://b.test", "b.test");
        }, Seeds.Passkey),
        new("microsoft-mail", b => { _ = OpenBook.Application.Forms.MicrosoftMail.Token(Text(b)); }, Seeds.MicrosoftToken),
        new("microsoft-mail-error", b => { _ = OpenBook.Application.Forms.MicrosoftMail.Error(Text(b), 400); }, Seeds.MicrosoftToken),
        new("mailbox-message", b =>
        {
            MimeKit.MimeMessage message;
            try
            {
                using var stream = new MemoryStream(b);
                message = MimeKit.MimeMessage.Load(stream);
            }
            catch (FormatException)
            {
                // MimeKit's own refusal; the mail server has read the message by then.
                return;
            }

            OpenBook.Infrastructure.Documents.MailboxFiles.Read(1, message);
        }, Seeds.BillMessage),
        new("plaid", b => { Plaid.TransactionsPage(Text(b)); Plaid.Accounts(Text(b)); _ = Plaid.Error(Text(b), 400); }, Seeds.Plaid),
        new("paypal", b =>
        {
            OpenBook.Application.Sales.PayPal.Order(Text(b));
        }, Seeds.PayPalOrder),
        new("paypal-event", b => { OpenBook.Application.Sales.PayPal.Event(Text(b)); _ = OpenBook.Application.Sales.PayPal.Error(Text(b), 422); _ = OpenBook.Application.Sales.PayPal.Issue(Text(b)); },
            Seeds.PayPalEvent),
        new("twilio", b =>
        {
            try
            {
                OpenBook.Application.Forms.Twilio.MessageId(Text(b));
            }
            catch (DomainException)
            {
            }

            _ = OpenBook.Application.Forms.Twilio.Error(Text(b), 400);
        }, Seeds.TwilioMessage, Seeds.TwilioError),
        new("phone-number", b => { _ = PhoneNumbers.TryNormalize(Text(b), "en-GB"); _ = TextTemplates.Measure(Text(b)); }, Seeds.PhoneNumber),
        new("paypal-refund", b => { OpenBook.Application.Sales.PayPal.Refund(Text(b)); OpenBook.Application.Sales.PayPal.FindRefund(Text(b), "obr-1"); }, Seeds.PayPalRefund),
        new("square-refund", b =>
        {
            OpenBook.Application.Sales.Square.Refund(Text(b));
            OpenBook.Application.Sales.Square.RefundReason(Text(b));
            OpenBook.Application.Sales.Square.RefundIds(Text(b));
        }, Seeds.SquareRefund),
        new("stripe-refund", b => { OpenBook.Application.Sales.StripeAnswers.Refund(Text(b)); OpenBook.Application.Sales.StripeAnswers.FindRefund(Text(b), "obr-1"); }, Seeds.StripeRefund),
        new("stripe-charge", b =>
        {
            _ = OpenBook.Application.Sales.StripeAnswers.Error(Text(b), 400);
            OpenBook.Application.Sales.StripeAnswers.ChargeEvent(Text(b));
            OpenBook.Application.Sales.StripeAnswers.Declined(Text(b));
            OpenBook.Application.Sales.StripeAnswers.FindIntent(Text(b), "obc-1");
        }, Seeds.StripeCharge),
        new("stripe-setup", b => { OpenBook.Application.Sales.StripeAnswers.Setup(Text(b)); }, Seeds.StripeSetup),
        new("square", b => { OpenBook.Application.Sales.Square.Order(Text(b), ""); }, Seeds.SquareOrder),
        new("square-event", b => { OpenBook.Application.Sales.Square.Event(Text(b)); _ = OpenBook.Application.Sales.Square.Error(Text(b), 400); }, Seeds.SquareEvent),
        new("update-manifest", b => UpdateManifest.Parse(Text(b)), Seeds.Manifest),
        new("report-query", b => BuilderOptions.Parse(ReportParameters.Parse(Text(b))), Seeds.ReportQuery),
        new("address", b => AddressParts.Parse(Text(b)), Seeds.Address),
        new("lots", b => { LotText.Parse(Text(b), ItemTracking.Lot, 5m, "Mug"); LotText.Parse(Text(b), ItemTracking.Serial, 3m, "Laptop"); }, Seeds.Lots),
        new("email-addresses", b => EmailAddresses.Parse(Text(b), "To"), Seeds.Emails),
        new("font-cmap", b => OpenBook.Application.Forms.FontCoverage.Read(b).Missing(["Acme 山田 م 😀 Z"]))
        {
            Binary = [Seeds.MinimalFont(4), Seeds.MinimalFont(12)],
        },
        new("gift-history", b => OpenBook.Application.Nonprofit.GiftHistoryService.Read(Text(b),
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["Pat Lee"] = Guid.NewGuid() }, DateTimeOffset.UnixEpoch), Seeds.GiftHistory),
        new("audit-requests", b => OpenBook.Application.Reporting.AuditRequestService.Read(2025, Text(b), ["Trial balance"], DateTimeOffset.UnixEpoch), Seeds.AuditRequests),
    ];

    public static TheoryData<string> Names() => [.. Targets.Select(t => t.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Damaged_input_is_read_or_refused_cleanly(string name)
    {
        var target = Targets.Single(t => t.Name == name);
        var seeds = target.Seeds.Select(s => Encoding.UTF8.GetBytes(s)).Concat(target.Binary).ToArray();
        var failures = new Dictionary<string, (int Case, string Detail, byte[] Input)>();

        // The samples themselves must read.
        for (var i = 0; i < seeds.Length; i++)
            if (Run(target, seeds[i]) is { } problem)
                failures.TryAdd($"sample {i + 1}: {problem.Key}", (-1 - i, problem.Detail, seeds[i]));

        var baseSeed = StableHash(name);
        for (var n = 0; n < Cases && failures.Count < 20; n++)
        {
            var random = new Random(unchecked(baseSeed + n * 7919));
            var input = Mutator.Mutate(seeds[random.Next(seeds.Length)], random);
            if (Run(target, input) is { } problem)
                failures.TryAdd(problem.Key, (n, problem.Detail, input));
        }

        if (failures.Count == 0)
            return;
        var folder = SaveFolder(name);
        var report = new StringBuilder($"{failures.Count} kind(s) of failure in {name} (inputs saved in {folder}):{Environment.NewLine}");
        foreach (var (key, (number, detail, input)) in failures)
        {
            File.WriteAllBytes(Path.Combine(folder, $"case-{number}.txt"), input);
            report.AppendLine(CultureInfo.InvariantCulture, $"- case {number}: {key}{Environment.NewLine}  {detail}");
        }

        Assert.Fail(report.ToString());
    }

    /// <summary>Null when the reader read the input or refused it properly; otherwise what went wrong, grouped by kind.</summary>
    private static (string Key, string Detail)? Run(Target target, byte[] input)
    {
        var watch = Stopwatch.StartNew();
        var task = Task.Factory.StartNew(() => target.Read(input), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // Waits without throwing: a refusal is the reader's answer, looked at below.
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(Limit + Limit))
            return ("took too long (still running)", $"more than {(Limit + Limit).TotalSeconds:N0} s on {input.Length:N0} bytes");
        watch.Stop();
        var error = task.Exception?.InnerException;
        if (error is null or DomainException or DocumentReadException)
            return watch.Elapsed > Limit ? ($"slow ({watch.Elapsed.TotalSeconds:N1} s)", $"{input.Length:N0} bytes") : null;
        var frame = new StackTrace(error).GetFrames().Select(f => f.GetMethod()).FirstOrDefault(m => m?.DeclaringType?.Namespace?.StartsWith("OpenBook", StringComparison.Ordinal) == true);
        var where = frame is null ? "(framework)" : $"{frame.DeclaringType!.Name}.{frame.Name}";
        return ($"{error.GetType().Name} in {where}", error.Message.Length > 300 ? error.Message[..300] : error.Message);
    }

    // What the statement import does with each row's cells: dates in each order, amounts.
    private static void StatementLine(string text)
    {
        foreach (var record in Csv.Parse(text))
            foreach (var cell in record.Fields)
            {
                _ = StatementValues.TryParseAmount(cell, out _);
                foreach (var order in Enum.GetValues<DateOrder>())
                    _ = StatementValues.TryParseDate(cell, order, out _);
            }

        var cells = Csv.Parse(text).SelectMany(r => r.Fields).ToList();
        StatementValues.DetectDateOrder(cells, CultureInfo.GetCultureInfo("en-US"), out _);
    }

    private static int StableHash(string text)
    {
        var hash = 17;
        foreach (var c in text)
            hash = unchecked(hash * 31 + c);
        return hash;
    }

    private static string SaveFolder(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBook.slnx")))
            dir = dir.Parent;
        var folder = Path.Combine(dir?.FullName ?? Path.GetTempPath(), "artifacts", "fuzz", name);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
