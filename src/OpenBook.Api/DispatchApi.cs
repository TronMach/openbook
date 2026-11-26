using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Settlements;

namespace OpenBook.Api;

/// <param name="PayAccountId">Null finds or makes "Owner-Operator Pay" for a new one, and keeps the account on a change.</param>
public sealed record OwnerRequest(
    Guid VendorId, Guid? PayAccountId = null, Guid? ReceivableAccountId = null, Guid? ReserveAccountId = null, Guid? ClassId = null, string? Memo = null, bool? IsActive = null)
{
    public OwnerDetails ToDetails() => new(VendorId, PayAccountId, ReceivableAccountId, ReserveAccountId, ClassId, Memo);
}

public sealed record OwnerReserveRequest(
    string Name, ReserveMethod Method, decimal Rate, decimal? Target = null, decimal Opening = 0m, decimal? InterestPercent = null, bool IsActive = true)
{
    public OwnerReserveDraft ToDraft() => new(Name, Method, Rate, Target, Opening, InterestPercent, IsActive);
}

public sealed record OwnerDeductionRequest(
    string Name, Guid AccountId, DeductionMethod Method, decimal Rate, decimal? UpTo = null, int Priority = 10, DateOnly? StartsOn = null, DateOnly? EndsOn = null,
    bool CarryShort = true, bool IsActive = true)
{
    public OwnerDeductionDraft ToDraft() => new(Name, AccountId, Method, Rate, UpTo, Priority, StartsOn, EndsOn, CarryShort, IsActive);
}

public sealed record OwnerDeductionResponse(
    int Number, string Name, Guid AccountId, DeductionMethod Method, decimal Rate, decimal? UpTo, int Priority, DateOnly? StartsOn, DateOnly? EndsOn, bool CarryShort, bool IsActive);

/// <param name="Month">The month's last day.</param>
public sealed record ReserveInterestRequest(DateOnly Month, Guid? ExpenseAccountId = null);

public sealed record OwnerReserveResponse(int Number, string Name, ReserveMethod Method, decimal Rate, decimal? Target, decimal Opening, decimal? InterestPercent, bool IsActive);

public sealed record OwnerResponse(
    Guid Id, string Name, Guid VendorId, Guid PayAccountId, Guid ReceivableAccountId, Guid ReserveAccountId, Guid? ClassId, string? Memo, bool IsActive,
    IReadOnlyList<OwnerReserveResponse> Reserves, IReadOnlyList<OwnerDeductionResponse>? Deductions = null, PayBasis PayBasis = PayBasis.Typed, decimal PayRate = 0m,
    bool PayEmptyMiles = false, IReadOnlyList<OwnerPassThroughRequest>? PassThroughs = null)
{
    public static OwnerResponse From(OwnerView v) => new(
        v.Owner.Id, v.Name, v.Owner.VendorId, v.Owner.PayAccountId, v.Owner.ReceivableAccountId, v.Owner.ReserveAccountId, v.Owner.ClassId, v.Owner.Memo, v.Owner.IsActive,
        v.Owner.Reserves.Select(r => new OwnerReserveResponse(r.Number, r.Name, r.Method, r.Rate, r.Target, r.Opening, r.InterestPercent, r.IsActive)).ToList(),
        v.Owner.Deductions.Select(d => new OwnerDeductionResponse(d.Number, d.Name, d.AccountId, d.Method, d.Rate, d.UpTo, d.Priority, d.StartsOn, d.EndsOn, d.CarryShort, d.IsActive)).ToList(),
        v.Owner.PayBasis, v.Owner.PayRate, v.Owner.PayEmptyMiles, v.Owner.PassThroughs.Select(p => new OwnerPassThroughRequest(p.Charge, p.Percent)).ToList());
}

public sealed record TruckRequest(string Unit, Guid? OwnerId = null, Guid? ClassId = null, string? Vin = null, string? Plate = null, int? Year = null, string? Make = null, bool? IsActive = null)
{
    public TruckDetails ToDetails() => new(Unit, OwnerId, ClassId, Vin, Plate, Year, Make);
}

public sealed record TruckResponse(Guid Id, string Unit, Guid? OwnerId, Guid? ClassId, string? Vin, string? Plate, int? Year, string? Make, bool IsActive)
{
    public static TruckResponse From(Truck t) => new(t.Id, t.Unit, t.OwnerId, t.ClassId, t.Vin, t.Plate, t.Year, t.Make, t.IsActive);
}

/// <param name="PaidFromAccountId">Null when it's already in the books, on the owner's receivable account.</param>
public sealed record OwnerChargeRequest(
    Guid OwnerId, DateOnly Date, OwnerChargeKind Kind, decimal Amount, Guid? PaidFromAccountId, string? Description = null, string? Reference = null, Guid? TruckId = null,
    decimal? Gallons = null, bool Counted1099 = false)
{
    public OwnerChargeDetails ToDetails() => new(OwnerId, Date, Kind, Amount, PaidFromAccountId, Description, Reference, TruckId, Gallons, Counted1099);
}

public sealed record OwnerChargeResponse(
    Guid Id, Guid OwnerId, DateOnly Date, OwnerChargeKind Kind, decimal Amount, decimal Recovered, decimal Balance, string? Description, string? Reference, Guid? TruckId,
    decimal? Gallons, Guid? PaidFromAccountId, Guid? JournalEntryId, Guid? FromSettlementId, bool Counted1099, bool IsVoided)
{
    public static OwnerChargeResponse From(OwnerCharge c) => new(
        c.Id, c.OwnerId, c.Date, c.Kind, c.Amount, c.Recovered, c.Balance, c.Description, c.Reference, c.TruckId, c.Gallons, c.PaidFromAccountId, c.JournalEntryId,
        c.FromSettlementId, c.Counted1099, c.IsVoided);
}

public sealed record OwnerSettlementLineResponse(
    int Line, OwnerLineKind Kind, string Description, decimal Amount, Guid AccountId, Guid? ClassId, string? Load, Guid? TruckId, decimal? LoadedMiles, decimal? EmptyMiles,
    bool Reportable, Guid? ChargeId, int? ReserveNumber, int? DeductionNumber, Guid? LoadId, int? DispatchNumber, decimal? Quantity, decimal? Rate);

public sealed record OwnerSettlementResponse(
    Guid Id, int Number, Guid OwnerId, string OwnerName, DateOnly Date, DateOnly? PeriodStart, DateOnly PeriodEnd, OwnerSettlementStatus Status, decimal Net, decimal Earned,
    decimal ReportableGross, decimal Withheld, Guid? BillId, Guid? JournalEntryId, bool IsLatest, string? Memo, IReadOnlyList<OwnerSettlementLineResponse>? Lines)
{
    public static OwnerSettlementResponse From(OwnerSettlementView v, bool lines = false) => new(
        v.Settlement.Id, v.Settlement.Number, v.Settlement.OwnerId, v.OwnerName, v.Settlement.Date, v.Settlement.PeriodStart, v.Settlement.PeriodEnd, v.Settlement.Status,
        v.Settlement.Net, v.Settlement.Earned, v.Settlement.ReportableGross, v.Settlement.Withheld, v.Settlement.BillId, v.Settlement.JournalEntryId, v.IsLatest, v.Settlement.Memo,
        lines ? v.Settlement.Lines.Select(l => new OwnerSettlementLineResponse(l.LineNumber, l.Kind, l.Description, l.Amount, l.AccountId, l.ClassId, l.Load, l.TruckId,
            l.LoadedMiles, l.EmptyMiles, l.Reportable, l.ChargeId, l.ReserveNumber, l.DeductionNumber, l.LoadId, l.DispatchNumber, l.Quantity, l.Rate)).ToList() : null);
}

/// <summary>What a run would make for one owner.</summary>
public sealed record OwnerPlanResponse(Guid OwnerId, string OwnerName, decimal Gross, decimal ReportableGross, decimal Net, decimal StillOpen, IReadOnlyList<string> Notes,
    IReadOnlyList<OwnerLineDraft> Lines)
{
    public static OwnerPlanResponse From(OwnerPlan p) => new(p.Owner.Owner.Id, p.Owner.Name, p.Gross, p.ReportableGross, p.Net, p.StillOpen, p.Notes, p.Lines);
}

/// <summary>A load as its rate confirmation has it. Changing one replaces its stops, charges and trucks; recorded stop times stay on unchanged stops.</summary>
public sealed record LoadRequest(
    Guid CustomerId, string? Reference, IReadOnlyList<LoadStopDraft>? Stops, IReadOnlyList<LoadChargeDraft>? Charges, IReadOnlyList<DispatchDraft>? Dispatches = null,
    decimal? Miles = null, string? Commodity = null, decimal? Weight = null, string? Memo = null)
{
    public LoadDetails ToDetails() => new(CustomerId, Reference, Stops ?? [], Charges ?? [], Dispatches ?? [], Miles, Commodity, Weight, Memo);
}

public sealed record LoadStopTimesRequest(DateTimeOffset? Arrived, DateTimeOffset? Departed);

public sealed record LoadInvoiceRequest(DateOnly? Date = null);

/// <param name="FreeHours">Free time at each stop.</param>
public sealed record LoadDetentionRequest(decimal FreeHours, decimal Rate, Guid? ItemId = null);

/// <summary>The owner's share of a kind of load charge other than linehaul.</summary>
public sealed record OwnerPassThroughRequest(LoadCharge Charge, decimal Percent);

public sealed record OwnerPayRequest(PayBasis Basis, decimal Rate = 0m, bool EmptyMiles = false, IReadOnlyList<OwnerPassThroughRequest>? PassThroughs = null);

public sealed record LoadStatusRequest(LoadStatus Status);

public sealed record LoadResponse(
    Guid Id, int Number, Guid CustomerId, string Customer, string? Reference, LoadStatus Status, decimal? Miles, string? Commodity, decimal? Weight, string? Memo, decimal Total,
    Guid? InvoiceId, IReadOnlyList<LoadStop> Stops, IReadOnlyList<LoadChargeLine> Charges, IReadOnlyList<Dispatch> Dispatches, IReadOnlyList<string> Warnings)
{
    public static LoadResponse From(LoadView v) => new(
        v.Load.Id, v.Load.Number, v.Load.CustomerId, v.Customer, v.Load.Reference, v.Load.Status, v.Load.Miles, v.Load.Commodity, v.Load.Weight, v.Load.Memo, v.Load.Total,
        v.Load.InvoiceId, v.Load.Stops, v.Load.Charges, v.Load.Dispatches, v.Load.Warnings());
}

public static class DispatchApi
{
    public static void MapDispatchApi(this RouteGroupBuilder api)
    {
        var dispatch = api.MapGroup("/dispatch");
        dispatch.MapGet("/owners", async (OwnerService s, CancellationToken ct) => (await s.ListOwnersAsync(includeInactive: true, ct)).Select(OwnerResponse.From));
        dispatch.MapGet("/owners/{id:guid}", async (Guid id, OwnerService s, CancellationToken ct) =>
            await s.GetOwnerAsync(id, ct) is { } o ? Results.Ok(OwnerResponse.From(o)) : Results.NotFound());
        dispatch.MapPost("/owners", async (OwnerRequest r, OwnerService s, CancellationToken ct) =>
        {
            var owner = await s.CreateOwnerAsync(r.ToDetails(), ct);
            return Results.Created($"/api/dispatch/owners/{owner.Id}", OwnerResponse.From((await s.GetOwnerAsync(owner.Id, ct))!));
        });
        dispatch.MapPut("/owners/{id:guid}", async (Guid id, OwnerRequest r, OwnerService s, CancellationToken ct) =>
        {
            var current = (await s.GetOwnerAsync(id, ct))?.Owner;
            await s.UpdateOwnerAsync(id, r.ToDetails() with { ClassId = ApiKeep.Id(r.ClassId, current?.ClassId), Memo = r.Memo ?? current?.Memo }, r.IsActive ?? current?.IsActive ?? true, ct);
            return OwnerResponse.From((await s.GetOwnerAsync(id, ct))!);
        });
        // Number 0 adds a reserve.
        dispatch.MapPut("/owners/{id:guid}/reserves/{number:int}", async (Guid id, int number, OwnerReserveRequest r, OwnerService s, CancellationToken ct) =>
        {
            await s.SetReserveAsync(id, number, r.ToDraft(), ct);
            return OwnerResponse.From((await s.GetOwnerAsync(id, ct))!);
        });
        // Number 0 adds a deduction.
        dispatch.MapPut("/owners/{id:guid}/deductions/{number:int}", async (Guid id, int number, OwnerDeductionRequest r, OwnerService s, CancellationToken ct) =>
        {
            await s.SetDeductionAsync(id, number, r.ToDraft(), ct);
            return OwnerResponse.From((await s.GetOwnerAsync(id, ct))!);
        });
        dispatch.MapGet("/loads", async (bool? all, LoadService s, CancellationToken ct) => (await s.ListAsync(all == true, ct)).Select(LoadResponse.From));
        dispatch.MapGet("/loads/{id:guid}", async (Guid id, LoadService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(LoadResponse.From(v)) : Results.NotFound());
        dispatch.MapPost("/loads", async (LoadRequest r, LoadService s, CancellationToken ct) =>
        {
            var load = await s.CreateAsync(r.ToDetails(), ct);
            return Results.Created($"/api/dispatch/loads/{load.Id}", LoadResponse.From((await s.GetAsync(load.Id, ct))!));
        });
        dispatch.MapPut("/loads/{id:guid}", async (Guid id, LoadRequest r, LoadService s, CancellationToken ct) =>
        {
            await s.ChangeAsync(id, r.ToDetails(), ct);
            return LoadResponse.From((await s.GetAsync(id, ct))!);
        });
        dispatch.MapPost("/loads/{id:guid}/stops/{sequence:int}", async (Guid id, int sequence, LoadStopTimesRequest r, LoadService s, CancellationToken ct) =>
        {
            await s.RecordStopAsync(id, sequence, r.Arrived, r.Departed, ct);
            return LoadResponse.From((await s.GetAsync(id, ct))!);
        });
        dispatch.MapPost("/loads/{id:guid}/status", async (Guid id, LoadStatusRequest r, LoadService s, CancellationToken ct) =>
        {
            await s.SetStatusAsync(id, r.Status, ct);
            return LoadResponse.From((await s.GetAsync(id, ct))!);
        });
        dispatch.MapPost("/loads/{id:guid}/invoice", async (Guid id, LoadInvoiceRequest r, LoadService s, CancellationToken ct) =>
        {
            var invoice = await s.InvoiceAsync(id, r.Date, ct);
            return Results.Created($"/api/sales/invoices/{invoice.Id}", new { invoice.Id, invoice.Number, invoice.Total });
        });
        dispatch.MapPost("/loads/{id:guid}/detention", async (Guid id, LoadDetentionRequest r, LoadService s, CancellationToken ct) =>
        {
            await s.ChargeDetentionAsync(id, r.FreeHours, r.Rate, r.ItemId, ct);
            return LoadResponse.From((await s.GetAsync(id, ct))!);
        });
        dispatch.MapPut("/owners/{id:guid}/pay", async (Guid id, OwnerPayRequest r, OwnerService s, CancellationToken ct) =>
        {
            await s.SetPayAsync(id, r.Basis, r.Rate, r.EmptyMiles, [.. (r.PassThroughs ?? []).Select(p => (p.Charge, p.Percent))], ct);
            return OwnerResponse.From((await s.GetOwnerAsync(id, ct))!);
        });
        dispatch.MapPost("/loads/{id:guid}/cancel", async (Guid id, LoadService s, CancellationToken ct) =>
        {
            await s.SetCancelledAsync(id, true, ct);
            return LoadResponse.From((await s.GetAsync(id, ct))!);
        });
        dispatch.MapPost("/loads/{id:guid}/restore", async (Guid id, LoadService s, CancellationToken ct) =>
        {
            await s.SetCancelledAsync(id, false, ct);
            return LoadResponse.From((await s.GetAsync(id, ct))!);
        });

        dispatch.MapGet("/interest", async (OwnerService s, CancellationToken ct) =>
            (await s.ListInterestRunsAsync(ct)).Select(r => new { r.Id, r.Date, r.Total, r.ExpenseAccountId, r.JournalEntryId, r.IsVoided,
                Lines = r.Lines.Select(l => new { l.OwnerId, l.ReserveNumber, l.Balance, l.Amount }) }));
        dispatch.MapGet("/interest/due", async (DateOnly month, OwnerService s, CancellationToken ct) => await s.InterestDueAsync(month, ct));
        dispatch.MapPost("/interest", async (ReserveInterestRequest r, OwnerService s, CancellationToken ct) =>
        {
            var run = await s.PostInterestAsync(r.Month, r.ExpenseAccountId, ct);
            return Results.Created($"/api/dispatch/interest/{run.Id}", new { run.Id, run.Date, run.Total, run.JournalEntryId });
        });
        dispatch.MapPost("/interest/{id:guid}/void", async (Guid id, VoidSettlementRequest r, OwnerService s, CancellationToken ct) =>
        {
            var run = await s.VoidInterestAsync(id, r.Reason, ct);
            return new { run.Id, run.Date, run.Total, run.IsVoided };
        });
        dispatch.MapGet("/owners/{id:guid}/reserves", async (Guid id, OwnerService s, CancellationToken ct) =>
            (await s.ReserveBalancesAsync(id, ct)).Select(b => new { b.Reserve.Number, b.Reserve.Name, b.Opening, b.Added, b.Released, b.Interest, b.Balance, b.Room }));

        dispatch.MapGet("/trucks", async (OwnerService s, CancellationToken ct) => (await s.ListTrucksAsync(includeInactive: true, ct)).Select(TruckResponse.From));
        dispatch.MapGet("/trucks/{id:guid}", async (Guid id, OwnerService s, CancellationToken ct) =>
            await s.GetTruckAsync(id, ct) is { } t ? Results.Ok(TruckResponse.From(t)) : Results.NotFound());
        dispatch.MapPost("/trucks", async (TruckRequest r, OwnerService s, CancellationToken ct) =>
        {
            var truck = await s.CreateTruckAsync(r.ToDetails(), ct);
            return Results.Created($"/api/dispatch/trucks/{truck.Id}", TruckResponse.From(truck));
        });
        dispatch.MapPut("/trucks/{id:guid}", async (Guid id, TruckRequest r, OwnerService s, CancellationToken ct) =>
        {
            var current = await s.GetTruckAsync(id, ct);
            return TruckResponse.From(await s.UpdateTruckAsync(id, r.ToDetails(), r.IsActive ?? current?.IsActive ?? true, ct));
        });

        dispatch.MapGet("/charges", async (Guid? ownerId, bool? open, OwnerService s, CancellationToken ct) =>
            (await s.ListChargesAsync(ownerId, open == true, ct)).Select(OwnerChargeResponse.From));
        dispatch.MapGet("/charges/{id:guid}", async (Guid id, OwnerService s, CancellationToken ct) =>
            await s.GetChargeAsync(id, ct) is { } c ? Results.Ok(OwnerChargeResponse.From(c)) : Results.NotFound());
        dispatch.MapPost("/charges", async (OwnerChargeRequest r, OwnerService s, CancellationToken ct) =>
        {
            var charge = await s.CreateChargeAsync(r.ToDetails(), ct);
            return Results.Created($"/api/dispatch/charges/{charge.Id}", OwnerChargeResponse.From(charge));
        });
        dispatch.MapGet("/settlements", async (Guid? ownerId, OwnerSettlementService s, CancellationToken ct) =>
            (await s.ListAsync(ownerId, ct)).Select(v => OwnerSettlementResponse.From(v)));
        dispatch.MapGet("/settlements/{id:guid}", async (Guid id, OwnerSettlementService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(OwnerSettlementResponse.From(v, lines: true)) : Results.NotFound());
        dispatch.MapPost("/settlements/preview", async (OwnerRunCommand r, OwnerSettlementService s, CancellationToken ct) =>
            (await s.PreviewAsync(r, ct)).Select(OwnerPlanResponse.From));
        dispatch.MapPost("/settlements/run", async (OwnerRunCommand r, OwnerSettlementService s, CancellationToken ct) =>
        {
            var made = await s.RunAsync(r, ct);
            var views = (await s.ListAsync(null, ct)).Where(v => made.Any(m => m.Id == v.Settlement.Id));
            return Results.Ok(views.Select(v => OwnerSettlementResponse.From(v, lines: true)));
        });
        dispatch.MapPost("/settlements/{id:guid}/void", async (Guid id, VoidSettlementRequest r, OwnerSettlementService s, CancellationToken ct) =>
        {
            await s.VoidAsync(id, r.Reason ?? "Settlement voided", ct);
            return OwnerSettlementResponse.From((await s.GetAsync(id, ct))!, lines: true);
        });

        dispatch.MapPost("/charges/{id:guid}/void", async (Guid id, VoidSettlementRequest r, OwnerService s, CancellationToken ct) =>
            OwnerChargeResponse.From(await s.VoidChargeAsync(id, r.Reason ?? "Charge voided", ct)));
    }
}
