using System.Text.Json.Nodes;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Application.SalesTax;

/// <summary>"We owe use tax on this", on a bill, an expense or a vendor credit.</summary>
/// <param name="TaxCodeId">The code whose rates are owed, on the document's date; <see cref="Guid.Empty"/> for no use tax.</param>
/// <param name="Amount">The tax as typed; null works it out from the rates.</param>
public sealed record UseTaxInput(Guid TaxCodeId, decimal? Amount = null);

/// <summary>What a document's use tax comes to, with the journal lines that go last in its entry.</summary>
internal sealed record UseTaxPlan(Guid TaxCodeId, decimal Taxable, IReadOnlyList<(TaxComponent Rate, decimal Amount)> Rates, IReadOnlyList<JournalLineDraft> Postings);

/// <summary>
/// Use tax on what the business buys without sales tax: worked out with the sales tax codes and their dated rates, posted
/// in the document's own entry, and owed to the agencies like the tax collected on sales.
/// </summary>
public sealed class UseTaxService(ISalesTaxStore store, SalesTaxService taxes, AccountService accounts)
{
    /// <summary>The document's use tax; null when there's none.</summary>
    public Task<UseTax?> ForAsync(Guid documentId, CancellationToken ct = default) => store.GetUseTaxAsync(documentId, ct);

    /// <summary>
    /// The codes to choose from at their rates on a date, the one use tax was last recorded with first: a company has one
    /// it nearly always owes.
    /// </summary>
    /// <param name="documentId">A document being corrected: its own code is there at the rates charged, even when it can't be chosen now.</param>
    public async Task<IReadOnlyList<TaxCodeChoice>> ChoicesAsync(DateOnly on, Guid? documentId = null, CancellationToken ct = default)
    {
        var choices = await taxes.ListChoicesAsync(on, ct);
        if (documentId is { } id && await store.GetUseTaxAsync(id, ct) is { } charged && await taxes.GetCodeAsync(charged.TaxCodeId, ct) is { } code)
        {
            return
            [
                new TaxCodeChoice(code, [.. charged.Lines.OrderBy(l => l.Position).Select(l => l.ToComponent())]),
                .. choices.Where(c => c.Code.Id != code.Id),
            ];
        }

        var last = await store.LastUseTaxCodeAsync(ct);
        return [.. choices.OrderByDescending(c => c.Code.Id == last)];
    }

    /// <summary>The part of a document's lines use tax is worked out on: categories that can carry it, not items received.</summary>
    public static decimal TaxableAmount(ChartOfAccounts chart, IEnumerable<(Guid AccountId, Guid? ItemId, decimal Amount)> lines)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(lines);
        return Carrying(chart, lines.Select(l => (l.AccountId, (Guid?)null, l.ItemId, l.Amount))).Sum(l => l.Amount);
    }

    private static List<(Guid AccountId, Guid? ClassId, decimal Amount)> Carrying(
        ChartOfAccounts chart, IEnumerable<(Guid AccountId, Guid? ClassId, Guid? ItemId, decimal Amount)> lines) =>
        lines.Where(l => l.ItemId is null && chart.Find(l.AccountId) is { } account && UseTax.CanCarry(account.Type))
            .Select(l => (l.AccountId, l.ClassId, l.Amount)).ToList();

    /// <summary>
    /// Works a document's use tax out, for its entry: everything's checked, nothing changes. A correction that says
    /// nothing keeps what there was, and under the same code the rates charged then.
    /// </summary>
    internal async Task<UseTaxPlan?> PlanAsync(
        UseTaxInput? input, UseTax? existing, UseTaxDocument kind, DateOnly date,
        IEnumerable<(Guid AccountId, Guid? ClassId, Guid? ItemId, decimal Amount)> lines, CancellationToken ct)
    {
        if (input is null && existing is null || input?.TaxCodeId == Guid.Empty)
            return null;
        var carrying = Carrying(await accounts.GetChartAsync(ct), lines);
        var taxable = carrying.Sum(l => l.Amount);
        if (taxable <= 0m)
            throw new DomainException("usetax.nothing_to_tax",
                "Use tax goes on expense and fixed asset lines, and there are none here to tax. Stock bought to resell isn't taxed when you buy it: untick use tax.");

        var codeId = input?.TaxCodeId ?? existing!.TaxCodeId;
        IReadOnlyList<TaxComponent> rates = existing is not null && existing.TaxCodeId == codeId
            ? [.. existing.Lines.OrderBy(l => l.Position).Select(l => l.ToComponent())]
            : await SalesTaxService.ComponentsAsync(store, codeId, ct, date);
        if (rates.Sum(r => r.Percent) == 0m)
            throw new DomainException("usetax.no_rate", "That tax code charges nothing on the document's date, so there's no use tax to record.");
        decimal? kept = existing is { IsTyped: true } && existing.Taxable == taxable ? existing.Total : null;
        var typed = input is null ? kept : input.Amount;
        var shares = TaxCalculator.Split(taxable, rates, _ => false, typed);
        if (shares.Sum() <= 0m)
            throw new DomainException("usetax.nothing", "The use tax comes to nothing. Type what's owed, or untick use tax.");
        var charged = rates.Select((r, i) => (Rate: r, Amount: shares[i])).ToList();
        return new UseTaxPlan(codeId, taxable, charged, UseTax.Postings(kind, carrying, charged));
    }

    /// <summary>Keeps the planned use tax with the document (or takes it off), once its entry is staged. Saved with it.</summary>
    internal UseTax? Apply(UseTaxPlan? plan, UseTax? existing, UseTaxDocument kind, Guid documentId, DateOnly date, JournalEntry entry)
    {
        if (plan is null)
        {
            if (existing is not null)
                store.Remove(existing);
            return null;
        }

        if (existing is null)
            store.Add(existing = UseTax.Create(kind, documentId));
        existing.Set(date, plan.TaxCodeId, plan.Taxable, plan.Rates, entry.Id, entry.Lines.Count - plan.Postings.Count + 1);
        return existing;
    }

    /// <summary>The document is voided, so its use tax is too; its entry's reversal takes the tax back off the accounts.</summary>
    internal async Task VoidAsync(Guid documentId, CancellationToken ct) => (await store.GetUseTaxAsync(documentId, ct))?.Void();

    /// <summary>A document's audit snapshot with its use tax.</summary>
    internal static string Snapshot(string document, UseTax? tax)
    {
        if (tax is null)
            return document;
        var json = JsonNode.Parse(document)!.AsObject();
        json["UseTax"] = new JsonObject
        {
            ["TaxCodeId"] = tax.TaxCodeId,
            ["Taxable"] = tax.Taxable,
            ["Tax"] = tax.Total,
        };
        return json.ToJsonString();
    }
}
