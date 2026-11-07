using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Assets;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Common;

namespace OpenBook.Api;

public sealed record FixedAssetResponse(
    Guid Id, string Name, string? Number, Guid AssetAccountId, Guid AccumulatedAccountId, Guid ExpenseAccountId, DateOnly InServiceDate, decimal Cost,
    decimal Salvage, DepreciationMethod Method, int LifeMonths, decimal DecliningPercent, MacrsClass? MacrsClass, decimal PriorDepreciation,
    DateOnly? PriorThrough, FixedAssetStatus Status, DateOnly? DisposedOn, decimal Proceeds, decimal Accumulated, decimal BookValue, string? Description = null)
{
    public static FixedAssetResponse From(AssetPosition p)
    {
        var a = p.Asset;
        return new(a.Id, a.Name, a.Number, a.AssetAccountId, a.AccumulatedAccountId, a.ExpenseAccountId, a.InServiceDate, a.Cost, a.Salvage, a.Method,
            a.LifeMonths, a.DecliningPercent, a.MacrsClass, a.PriorDepreciation, a.PriorThrough, a.Status, a.DisposedOn, a.Proceeds, p.Accumulated, p.BookValue, a.Description);
    }
}

public sealed record DepreciationRunResponse(Guid Id, DateOnly Date, DocumentStatus Status, Guid JournalEntryId, decimal Total, IReadOnlyList<DepreciationLineResponse> Lines)
{
    public static DepreciationRunResponse From(DepreciationRun r) =>
        new(r.Id, r.Date, r.Status, r.JournalEntryId, r.Total, r.Lines.Select(l => new DepreciationLineResponse(l.AssetId, l.Amount)).ToList());
}

public sealed record DepreciationLineResponse(Guid AssetId, decimal Amount);

public sealed record LoanResponse(
    Guid Id, string Name, Guid LiabilityAccountId, Guid InterestAccountId, decimal Principal, decimal AnnualRatePercent, int Payments,
    DateOnly FirstPaymentDate, decimal PaymentAmount, decimal Balance, IReadOnlyList<LoanPaymentResponse> PaymentsMade, Guid? LenderVendorId = null, string? Notes = null, bool IsVoided = false)
{
    public static LoanResponse From(Loan l) => new(
        l.Id, l.Name, l.LiabilityAccountId, l.InterestAccountId, l.Principal, l.AnnualRatePercent, l.Payments, l.FirstPaymentDate, l.PaymentAmount, l.Balance,
        l.PaymentsMade.Select(p => new LoanPaymentResponse(p.Id, p.Date, p.Amount, p.Interest, p.Principal, p.PaymentAccountId, p.JournalEntryId, p.IsVoided)).ToList()) { LenderVendorId = l.LenderVendorId, Notes = l.Notes, IsVoided = l.IsVoided };
}

public sealed record LoanPaymentResponse(Guid Id, DateOnly Date, decimal Amount, decimal Interest, decimal Principal, Guid PaymentAccountId, Guid JournalEntryId, bool IsVoided);

public sealed record DepreciationRequest(DateOnly Through);

public static class AssetsApi
{
    public static void MapAssetsApi(this RouteGroupBuilder api)
    {
        var assets = api.MapGroup("/assets");

        assets.MapGet("", async (FixedAssetService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(FixedAssetResponse.From));
        assets.MapGet("/{id:guid}", async (Guid id, FixedAssetService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } p ? Results.Ok(FixedAssetResponse.From(p)) : Results.NotFound());
        assets.MapGet("/{id:guid}/schedule", async (Guid id, FixedAssetService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } p ? Results.Ok(Depreciation.Schedule(p.Asset)) : Results.NotFound());
        assets.MapPost("", async (FixedAssetDetails r, FixedAssetService s, CancellationToken ct) =>
        {
            var asset = await s.CreateAsync(r, ct);
            return Results.Created($"/api/assets/{asset.Id}", FixedAssetResponse.From((await s.GetAsync(asset.Id, ct))!));
        });
        assets.MapPut("/{id:guid}", async (Guid id, FixedAssetDetails r, FixedAssetService s, CancellationToken ct) =>
        {
            await s.UpdateAsync(id, r, ct);
            return FixedAssetResponse.From((await s.GetAsync(id, ct))!);
        });
        // An asset entered by mistake, before anything posted for it: off the register, and back.
        assets.MapPost("/{id:guid}/remove", async (Guid id, FixedAssetService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(id, ct);
            return FixedAssetResponse.From((await s.GetAsync(id, ct))!);
        });
        assets.MapPost("/{id:guid}/put-back", async (Guid id, FixedAssetService s, CancellationToken ct) =>
        {
            await s.PutBackAsync(id, ct);
            return FixedAssetResponse.From((await s.GetAsync(id, ct))!);
        });
        assets.MapPost("/{id:guid}/dispose", async (Guid id, DisposeAssetCommand r, FixedAssetService s, CancellationToken ct) =>
        {
            await s.DisposeAsync(id, r, ct);
            return FixedAssetResponse.From((await s.GetAsync(id, ct))!);
        });
        assets.MapPost("/{id:guid}/undispose", async (Guid id, VoidRequest r, FixedAssetService s, CancellationToken ct) =>
        {
            await s.UndoDisposalAsync(id, r.Reason, ct);
            return FixedAssetResponse.From((await s.GetAsync(id, ct))!);
        });

        assets.MapGet("/depreciation", async (DepreciationService s, CancellationToken ct) => (await s.ListRunsAsync(ct)).Select(DepreciationRunResponse.From));
        assets.MapGet("/depreciation/preview", async (DateOnly through, DepreciationService s, CancellationToken ct) =>
            (await s.PreviewAsync(through, ct)).Select(d => new DepreciationLineResponse(d.Asset.Id, d.Amount)));
        assets.MapPost("/depreciation", async (DepreciationRequest r, DepreciationService s, CancellationToken ct) =>
        {
            var run = await s.RunAsync(r.Through, ct);
            return Results.Created($"/api/assets/depreciation/{run.Id}", DepreciationRunResponse.From(run));
        });
        assets.MapPost("/depreciation/{id:guid}/void", async (Guid id, VoidRequest r, DepreciationService s, CancellationToken ct) =>
            DepreciationRunResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        assets.MapGet("/loans", async (LoanService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(LoanResponse.From));
        assets.MapGet("/loans/{id:guid}", async (Guid id, LoanService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } l ? Results.Ok(LoanResponse.From(l)) : Results.NotFound());
        assets.MapGet("/loans/{id:guid}/schedule", async (Guid id, LoanService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } l ? Results.Ok(LoanService.RemainingSchedule(l)) : Results.NotFound());
        assets.MapPost("/loans", async (CreateLoanCommand r, LoanService s, CancellationToken ct) =>
        {
            var loan = await s.CreateAsync(r, ct);
            return Results.Created($"/api/assets/loans/{loan.Id}", LoanResponse.From(loan));
        });
        assets.MapPut("/loans/{id:guid}", async (Guid id, LoanDetails r, LoanService s, CancellationToken ct) => LoanResponse.From(await s.UpdateAsync(id, r, ct)));
        assets.MapPost("/loans/{id:guid}/payments", async (Guid id, LoanPaymentCommand r, LoanService s, CancellationToken ct) =>
        {
            await s.PayAsync(id, r, ct);
            return LoanResponse.From((await s.GetAsync(id, ct))!);
        });
        assets.MapPost("/loans/{id:guid}/void", async (Guid id, VoidRequest r, LoanService s, CancellationToken ct) =>
            LoanResponse.From(await s.VoidAsync(id, r.Reason, ct)));
        assets.MapPost("/loans/{id:guid}/payments/{paymentId:guid}/void", async (Guid id, Guid paymentId, VoidRequest r, LoanService s, CancellationToken ct) =>
            LoanResponse.From(await s.VoidPaymentAsync(id, paymentId, r.Reason, ct)));
    }
}
