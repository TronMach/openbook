using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.SalesTax;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Api;

public sealed record TaxAgencyResponse(
    Guid Id, string Name, Guid LiabilityAccountId, FilingFrequency FilingFrequency, string? RegistrationNumber, bool IsActive, decimal? Owed = null)
{
    public static TaxAgencyResponse From(TaxAgency a, decimal? owed = null) =>
        new(a.Id, a.Name, a.LiabilityAccountId, a.FilingFrequency, a.RegistrationNumber, a.IsActive, owed);
}

public sealed record TaxRateResponse(Guid Id, string Name, Guid AgencyId, decimal Percent, bool IsActive)
{
    public static TaxRateResponse From(TaxRate r) => new(r.Id, r.Name, r.AgencyId, r.Percent, r.IsActive);
}

public sealed record TaxCodeResponse(Guid Id, string Name, string? Description, IReadOnlyList<Guid> RateIds, bool IsCombined, bool IsActive)
{
    public static TaxCodeResponse From(TaxCode c) => new(c.Id, c.Name, c.Description, c.RateIds, c.IsCombined, c.IsActive);
}

public sealed record SalesTaxPaymentResponse(
    Guid Id, Guid AgencyId, DateOnly Date, DateOnly? PeriodEnd, decimal Amount, Guid PaymentAccountId, decimal Adjustment, Guid? AdjustmentAccountId,
    string? Reference, string? Memo, DocumentStatus Status, Guid JournalEntryId)
{
    public static SalesTaxPaymentResponse From(SalesTaxPayment p) => new(
        p.Id, p.AgencyId, p.Date, p.PeriodEnd, p.Amount, p.PaymentAccountId, p.Adjustment, p.AdjustmentAccountId, p.Reference, p.Memo, p.Status, p.JournalEntryId);
}

/// <param name="CreateCode">Also create a tax code of the same name charging just this rate.</param>
public sealed record NexusThresholdRequest(string Region, decimal SalesAmount, int? Transactions = null);

public sealed record CreateTaxRateRequest(string Name, Guid AgencyId, decimal Percent, bool CreateCode = true);

public sealed record TaxLiabilityAgencyResponse(
    Guid AgencyId, string Agency, IReadOnlyList<TaxLiabilityRow> Rates, decimal Tax, decimal OwedAtStart, decimal Paid, decimal OwedAtEnd);

public sealed record TaxLiabilityResponse(DateOnly From, DateOnly To, IReadOnlyList<TaxLiabilityAgencyResponse> Agencies, decimal UntaxedSales);

internal static class SalesTaxApi
{
    public static void MapSalesTaxApi(this RouteGroupBuilder api)
    {
        var tax = api.MapGroup("/sales-tax");

        tax.MapGet("/agencies", async (DateOnly? asOf, SalesTaxService s, SalesTaxPaymentService p, CancellationToken ct) =>
        {
            var owed = await p.OwedAsync(asOf ?? CompanyTime.Today, ct: ct);
            return (await s.ListAgenciesAsync(ct)).Select(a => TaxAgencyResponse.From(a, owed.GetValueOrDefault(a.Id)));
        });
        tax.MapGet("/agencies/{id:guid}", async (Guid id, SalesTaxService s, CancellationToken ct) =>
            await s.GetAgencyAsync(id, ct) is { } a ? Results.Ok(TaxAgencyResponse.From(a)) : Results.NotFound());
        tax.MapPost("/agencies", async (TaxAgencyDetails r, SalesTaxService s, CancellationToken ct) =>
        {
            var a = await s.CreateAgencyAsync(r, ct);
            return Results.Created($"/api/sales-tax/agencies/{a.Id}", TaxAgencyResponse.From(a));
        });
        tax.MapPut("/agencies/{id:guid}", async (Guid id, TaxAgencyDetails r, SalesTaxService s, CancellationToken ct) =>
            TaxAgencyResponse.From(await s.UpdateAgencyAsync(id, r, ct)));
        tax.MapPut("/agencies/{id:guid}/active", async (Guid id, SetActiveRequest r, SalesTaxService s, CancellationToken ct) =>
            TaxAgencyResponse.From(await s.SetAgencyActiveAsync(id, r.Active, ct)));

        tax.MapGet("/rates", async (SalesTaxService s, CancellationToken ct) => (await s.ListRatesAsync(ct)).Select(TaxRateResponse.From));
        tax.MapGet("/rates/{id:guid}", async (Guid id, SalesTaxService s, CancellationToken ct) =>
            await s.GetRateAsync(id, ct) is { } r ? Results.Ok(TaxRateResponse.From(r)) : Results.NotFound());
        tax.MapPost("/rates", async (CreateTaxRateRequest r, SalesTaxService s, CancellationToken ct) =>
        {
            var rate = await s.CreateRateAsync(new TaxRateDetails(r.Name, r.AgencyId, r.Percent), r.CreateCode, ct);
            return Results.Created($"/api/sales-tax/rates/{rate.Id}", TaxRateResponse.From(rate));
        });
        tax.MapPut("/rates/{id:guid}", async (Guid id, TaxRateDetails r, SalesTaxService s, CancellationToken ct) =>
            TaxRateResponse.From(await s.UpdateRateAsync(id, r, ct)));
        tax.MapPut("/rates/{id:guid}/active", async (Guid id, SetActiveRequest r, SalesTaxService s, CancellationToken ct) =>
            TaxRateResponse.From(await s.SetRateActiveAsync(id, r.Active, ct)));

        tax.MapGet("/codes", async (SalesTaxService s, CancellationToken ct) => (await s.ListCodesAsync(ct)).Select(TaxCodeResponse.From));
        tax.MapGet("/codes/{id:guid}", async (Guid id, SalesTaxService s, CancellationToken ct) =>
            await s.GetCodeAsync(id, ct) is { } c ? Results.Ok(TaxCodeResponse.From(c)) : Results.NotFound());
        tax.MapPost("/codes", async (TaxCodeDetails r, SalesTaxService s, CancellationToken ct) =>
        {
            var c = await s.CreateCodeAsync(r, ct);
            return Results.Created($"/api/sales-tax/codes/{c.Id}", TaxCodeResponse.From(c));
        });
        tax.MapPut("/codes/{id:guid}", async (Guid id, TaxCodeDetails r, SalesTaxService s, CancellationToken ct) =>
            TaxCodeResponse.From(await s.UpdateCodeAsync(id, r, ct)));
        tax.MapPut("/codes/{id:guid}/active", async (Guid id, SetActiveRequest r, SalesTaxService s, CancellationToken ct) =>
            TaxCodeResponse.From(await s.SetCodeActiveAsync(id, r.Active, ct)));

        tax.MapGet("/payments", async (Guid? agencyId, SalesTaxPaymentService s, CancellationToken ct) =>
            (await s.ListAsync(agencyId, ct)).Select(SalesTaxPaymentResponse.From));
        tax.MapGet("/payments/{id:guid}", async (Guid id, SalesTaxPaymentService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } p ? Results.Ok(SalesTaxPaymentResponse.From(p)) : Results.NotFound());
        tax.MapPost("/payments", async (PaySalesTaxCommand r, SalesTaxPaymentService s, CancellationToken ct) =>
        {
            var p = await s.PayAsync(r, ct);
            return Results.Created($"/api/sales-tax/payments/{p.Id}", SalesTaxPaymentResponse.From(p));
        });
        tax.MapPost("/payments/{id:guid}/void", async (Guid id, VoidRequest r, SalesTaxPaymentService s, CancellationToken ct) =>
            SalesTaxPaymentResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        tax.MapGet("/liability", async (DateOnly from, DateOnly to, string? basis, SalesTaxReports s, CancellationToken ct) =>
        {
            var report = await s.LiabilityAsync(from, to, string.Equals(basis, "cash", StringComparison.OrdinalIgnoreCase) ? ReportBasis.Cash : ReportBasis.Accrual, ct);
            return new TaxLiabilityResponse(report.From, report.To,
                report.Agencies.Select(a => new TaxLiabilityAgencyResponse(a.Agency.Id, a.Agency.Name, a.Rates, a.Tax, a.OwedAtStart, a.Paid, a.OwedAtEnd)).ToList(),
                report.UntaxedSales);
        });
        tax.MapGet("/nexus", async (NexusService s, CancellationToken ct) =>
            (await s.ThresholdsAsync(ct)).Select(t => new NexusThresholdRequest(t.Region, t.SalesAmount, t.Transactions)));
        tax.MapPut("/nexus", async (NexusThresholdRequest r, NexusService s, CancellationToken ct) =>
        {
            var t = await s.SaveAsync(r.Region, r.SalesAmount, r.Transactions, ct);
            return new NexusThresholdRequest(t.Region, t.SalesAmount, t.Transactions);
        });
        tax.MapDelete("/nexus/{region}", async (string region, NexusService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(region, ct);
            return Results.NoContent();
        });
        tax.MapGet("/sales-by-state", async (DateOnly from, DateOnly to, NexusService s, CancellationToken ct) =>
        {
            var report = await s.SalesByStateAsync(from, to, ct);
            return new
            {
                report.From, report.To, report.Unknown,
                States = report.States.Select(x => new { x.Region, x.Sales, x.Transactions, Threshold = x.Threshold?.SalesAmount, TransactionsThreshold = x.Threshold?.Transactions, x.OverThreshold, x.HasAgency }),
            };
        });
        tax.MapGet("/exempt-customers", async (SalesTaxReports s, CancellationToken ct) =>
            (await s.ExemptCustomersAsync(ct)).Select(x => new
            {
                x.Customer.Id,
                x.Customer.DisplayName,
                x.Customer.ExemptionCertificate,
                x.Customer.ExemptionReason,
                x.Customer.ExemptionExpiresOn,
                x.Expired,
                x.ExpiresSoon,
            }));
    }
}
