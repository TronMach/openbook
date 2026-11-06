using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

public enum CheckKind
{
    /// <summary>A spend (check or payment) recorded in banking or as an expense.</summary>
    Expense,
    BillPayment,
}

public sealed record PrintableCheck(CheckKind Kind, Guid Id, Guid AccountId, DateOnly Date, string? Number, string Payee, decimal Amount, DateTimeOffset? PrintedAt);

public sealed record PrintedChecks(string FileName, byte[] Pdf, int Count);

/// <summary>Printing checks from bank accounts: expenses and bill payments, on voucher check stock.</summary>
public sealed class CheckService(
    IBankingStore bank,
    IPurchasesStore purchases,
    AccountService accounts,
    ICompanyStore companies,
    IFormRenderer renderer,
    IClock clock,
    ICurrentUser user)
{
    /// <summary>
    /// Posted payments from bank accounts (not cards), newest first. Not payments sent by ACH, nor spends brought in from
    /// the bank's own lines: that money has already left, so a check would pay it again.
    /// </summary>
    public async Task<IReadOnlyList<PrintableCheck>> ListAsync(Guid? accountId = null, bool unprintedOnly = false, CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        bool IsBank(Guid id) => chart.Find(id)?.Type == AccountType.Bank && (accountId is null || accountId == id);
        var vendors = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);

        var expenses = (await bank.ListCheckSpendsAsync(ct))
            .Where(t => IsBank(t.AccountId))
            .Select(t => new PrintableCheck(CheckKind.Expense, t.Id, t.AccountId, t.Date, t.Reference,
                t.Payee ?? (t.VendorId is { } v ? vendors.GetValueOrDefault(v) : null) ?? "(no payee)", t.Amount, t.PrintedAt));
        var payments = (await purchases.ListCheckPaymentsAsync(ct))
            .Where(p => IsBank(p.PaymentAccountId))
            .Select(p => new PrintableCheck(CheckKind.BillPayment, p.Id, p.PaymentAccountId, p.Date, p.Reference,
                vendors.GetValueOrDefault(p.VendorId) ?? "(unknown vendor)", p.Amount, p.PrintedAt));

        return expenses.Concat(payments)
            .Where(c => !unprintedOnly || c.PrintedAt is null)
            .OrderByDescending(c => c.Date)
            .ThenByDescending(c => c.Number, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Renders the checks (one per page, in the order given) and records that they were printed.</summary>
    public async Task<PrintedChecks> PrintAsync(IReadOnlyList<(CheckKind Kind, Guid Id)> checks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count == 0)
            throw new DomainException("check.none", "Choose at least one check to print.");

        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var chart = await accounts.GetChartAsync(ct);
        var vendors = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id);
        var forms = new List<CheckForm>();
        var now = clock.UtcNow;

        foreach (var (kind, id) in checks.Distinct())
        {
            if (kind == CheckKind.Expense)
            {
                var t = await bank.GetTransactionAsync(id, ct) ?? throw new EntityNotFoundException("bank_transaction", id);
                if (t.Kind != BankTransactionKind.Spend || t.Status != BankTransactionStatus.Posted || chart.Get(t.AccountId).Type != AccountType.Bank)
                    throw new DomainException("check.not_printable", "Only posted payments from a bank account can be printed as checks.");
                var vendor = t.VendorId is { } v ? vendors.GetValueOrDefault(v) : null;
                var lines = await ExpenseLinesAsync(t, chart, ct);
                forms.Add(Form(company, chart.Get(t.AccountId).Name, t.Reference, t.Date,
                    new FormParty(t.Payee ?? vendor?.DisplayName ?? "", vendor?.Address), t.Amount, t.Memo ?? vendor?.AccountNumber, lines));
                var before = Snapshot(t.PrintedAt);
                t.MarkPrinted(now);
                bank.Add(AuditRecord.Create(now, user.Actor, AuditAction.Updated, "bank_transaction", t.Id, before, Snapshot(t.PrintedAt)));
            }
            else
            {
                var p = await purchases.GetPaymentAsync(id, ct) ?? throw new EntityNotFoundException("bill_payment", id);
                if (p.Status != DocumentStatus.Posted || chart.Get(p.PaymentAccountId).Type != AccountType.Bank)
                    throw new DomainException("check.not_printable", "Only posted payments from a bank account can be printed as checks.");
                if (p.AchBatchId is not null)
                    throw new DomainException("check.sent_by_ach", "This payment went out in an ACH file, so a check would pay it twice.");
                var vendor = vendors.GetValueOrDefault(p.VendorId);
                var bills = (await purchases.GetBillsAsync(p.Applications.Select(a => a.BillId).ToList(), ct)).ToDictionary(b => b.Id);
                var lines = p.Applications
                    .Select(a => (bills.GetValueOrDefault(a.BillId) is { } b ? $"Bill {b.Reference ?? b.Date.ToString("d", System.Globalization.CultureInfo.CurrentCulture)}" : "Bill", a.Amount))
                    .ToList();
                forms.Add(Form(company, chart.Get(p.PaymentAccountId).Name, p.Reference, p.Date,
                    new FormParty(vendor?.DisplayName ?? "", vendor?.Address), p.Amount, p.Memo, lines));
                var before = Snapshot(p.PrintedAt);
                p.MarkPrinted(now);
                bank.Add(AuditRecord.Create(now, user.Actor, AuditAction.Updated, "bill_payment", p.Id, before, Snapshot(p.PrintedAt)));
            }
        }

        var pdf = renderer.Render(forms);
        await bank.SaveChangesAsync(ct);
        var name = forms.Count == 1 && forms[0].Number is { } number ? $"Check {number}.pdf" : $"Checks {now:yyyy-MM-dd}.pdf";
        return new PrintedChecks(name, pdf, forms.Count);
    }

    private async Task<List<(string, decimal)>> ExpenseLinesAsync(BankTransaction t, Domain.Accounts.ChartOfAccounts chart, CancellationToken ct)
    {
        // A single category names what it was for; split payments just show the total.
        var counterparts = await bank.GetCounterpartAccountsAsync(t.AccountId, ct);
        return counterparts.GetValueOrDefault(t.JournalEntryId) is { Count: 1 } single
            ? [(chart.FullName(single[0]), t.Amount)]
            : [(t.Memo ?? "Payment", t.Amount)];
    }

    private static CheckForm Form(
        Domain.Companies.Company company, string bankName, string? number, DateOnly date, FormParty payee, decimal amount, string? memo,
        IReadOnlyList<(string, decimal)> lines) =>
        new(FormService.CompanyParty(company), bankName, number, date, payee, amount, AmountInWords.ForCheck(amount), memo, lines);

    private static string Snapshot(DateTimeOffset? printedAt) => JsonSerializer.Serialize(new { PrintedAt = printedAt });
}
