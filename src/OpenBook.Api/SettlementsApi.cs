using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Settlements;

namespace OpenBook.Api;

public sealed record CarrierRequest(
    string Name, Guid DepositAccountId, Guid? VendorId = null, Guid? DueAccountId = null, Guid? ClassId = null, Guid? LocationId = null, string? Unit = null, bool? IsActive = null);

public sealed record CarrierCodeResponse(SettlementSection Section, string Code, string? Name, Guid AccountId, Guid? ClassId);

public sealed record CarrierResponse(
    Guid Id, string Name, string? Unit, Guid DepositAccountId, Guid? VendorId, Guid? DueAccountId, Guid? ClassId, Guid? LocationId, bool IsActive,
    IReadOnlyList<CarrierCodeResponse> Codes)
{
    public static CarrierResponse From(Carrier c) => new(
        c.Id, c.Name, c.Unit, c.DepositAccountId, c.VendorId, c.DueAccountId, c.ClassId, c.LocationId, c.IsActive,
        c.Codes.Select(x => new CarrierCodeResponse(x.Section, x.Code, x.Name, x.AccountId, x.ClassId)).ToList());
}

public sealed record CarrierCodeRequest(SettlementSection Section, string Code, Guid AccountId, string? Name = null, Guid? ClassId = null);

/// <summary>Codes to add or change, and codes to forget; the carrier's others stay.</summary>
public sealed record CarrierCodesRequest(IReadOnlyList<CarrierCodeRequest>? Codes, IReadOnlyList<CarrierCodeName>? Forget = null);

public sealed record CarrierCodeName(SettlementSection Section, string Code);

/// <summary>Amounts are signed: positive is money to the owner, negative money taken.</summary>
public sealed record SettlementLineRequest(
    SettlementSection Section, string? Code, decimal Amount, DateOnly? Date = null, string? Load = null, string? Dispatch = null, string? Description = null,
    Guid? AccountId = null, Guid? ClassId = null, decimal? LoadedMiles = null, decimal? EmptyMiles = null, decimal? Gallons = null);

public sealed record SettlementDepositRequest(Guid AccountId, decimal Amount);

/// <summary>A section's printed total, signed like the lines.</summary>
public sealed record SettlementTotalRequest(SettlementSection Section, decimal Amount);

/// <summary>A settlement as its statement has it. Changing one replaces all of its lines and deposits.</summary>
/// <param name="CarrierId">Needed for a new one; a settlement never changes carrier.</param>
public sealed record SettlementRequest(
    string Number, DateOnly Date, decimal NetPaid, IReadOnlyList<SettlementLineRequest>? Lines, Guid? CarrierId = null, DateOnly? PeriodStart = null, DateOnly? PeriodEnd = null,
    decimal CarriedForward = 0m, IReadOnlyList<SettlementDepositRequest>? Deposits = null, string? Memo = null,
    IReadOnlyList<SettlementTotalRequest>? StatedTotals = null, IReadOnlyList<SettlementDepositRequest>? StatedBalances = null)
{
    public SettlementDetails ToDetails() => new(
        Number, Date, NetPaid,
        (Lines ?? []).Select(l => new SettlementLineDraft(l.Section, l.Code, l.Amount, l.Date, l.Load, l.Dispatch, l.Description, l.AccountId, l.ClassId, l.LoadedMiles, l.EmptyMiles, l.Gallons)).ToList(),
        PeriodStart, PeriodEnd, CarriedForward, (Deposits ?? []).Select(d => new SettlementDepositDraft(d.AccountId, d.Amount)).ToList(), Memo,
        (StatedTotals ?? []).Select(t => new SettlementTotalDraft(t.Section, t.Amount)).ToList(), (StatedBalances ?? []).Select(b => new SettlementBalanceDraft(b.AccountId, b.Amount)).ToList());
}

public sealed record SettlementLineResponse(
    int Line, SettlementSection Section, string Code, decimal Amount, DateOnly? Date, string? Load, string? Dispatch, string? Description, Guid? AccountId, Guid? ClassId,
    Guid? PostedAccountId, decimal? LoadedMiles, decimal? EmptyMiles, decimal? Gallons, string? Doubt, bool NeedsChecking);

public sealed record SettlementResponse(
    Guid Id, Guid CarrierId, string Number, DateOnly Date, DateOnly? PeriodStart, DateOnly? PeriodEnd, SettlementStatus Status, decimal NetPaid, decimal CarriedForward,
    decimal Difference, decimal LoadedMiles, decimal EmptyMiles, decimal Gallons, Guid? JournalEntryId, string? Memo,
    IReadOnlyDictionary<SettlementSection, decimal> Totals, IReadOnlyList<SettlementLineResponse>? Lines, IReadOnlyList<SettlementDepositRequest>? Deposits,
    IReadOnlyList<SettlementTotalRequest>? StatedTotals, IReadOnlyList<SettlementDepositRequest>? StatedBalances, string? ReadMethod, string? ReadNote, string? CheckedBy)
{
    public static SettlementResponse From(Settlement s, bool lines = false) => new(
        s.Id, s.CarrierId, s.Number, s.Date, s.PeriodStart, s.PeriodEnd, s.Status, s.NetPaid, s.CarriedForward, s.Difference, s.LoadedMiles, s.EmptyMiles, s.Gallons,
        s.JournalEntryId, s.Memo, s.Lines.GroupBy(l => l.Section).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount)),
        lines ? s.Lines.Select(l => new SettlementLineResponse(l.LineNumber, l.Section, l.Code, l.Amount, l.Date, l.Load, l.Dispatch, l.Description, l.AccountId, l.ClassId,
            l.PostedAccountId, l.LoadedMiles, l.EmptyMiles, l.Gallons, l.Doubt, l.NeedsChecking)).ToList() : null,
        lines ? s.Deposits.Select(d => new SettlementDepositRequest(d.AccountId, d.Amount)).ToList() : null,
        lines ? s.StatedTotals.Select(t => new SettlementTotalRequest(t.Section, t.Amount)).ToList() : null,
        lines ? s.StatedBalances.Select(b => new SettlementDepositRequest(b.AccountId, b.Amount)).ToList() : null, s.ReadMethod, s.ReadNote, s.CheckedBy);
}

public sealed record VoidSettlementRequest(string? Reason);

/// <summary>That a person checked a settlement read by OCR against its statement (<see cref="SettlementService.ConfirmAsync"/>).</summary>
public sealed record ConfirmSettlementRequest(decimal NetPaid, bool AllChecked, IReadOnlyList<int>? CheckedLines = null);

public static class SettlementsApi
{
    public static void MapSettlementsApi(this RouteGroupBuilder api)
    {
        var settlements = api.MapGroup("/settlements");
        settlements.MapGet("/carriers", async (SettlementService s, CancellationToken ct) => (await s.ListCarriersAsync(includeInactive: true, ct)).Select(CarrierResponse.From));
        settlements.MapGet("/carriers/{id:guid}", async (Guid id, SettlementService s, CancellationToken ct) =>
            await s.GetCarrierAsync(id, ct) is { } c ? Results.Ok(CarrierResponse.From(c)) : Results.NotFound());
        settlements.MapPost("/carriers", async (CarrierRequest r, SettlementService s, CancellationToken ct) =>
        {
            var carrier = await s.CreateCarrierAsync(new CarrierDetails(r.Name, r.DepositAccountId, r.VendorId, r.DueAccountId, r.ClassId, r.LocationId, r.Unit), ct);
            return Results.Created($"/api/settlements/carriers/{carrier.Id}", CarrierResponse.From(carrier));
        });
        settlements.MapPut("/carriers/{id:guid}", async (Guid id, CarrierRequest r, SettlementService s, CancellationToken ct) =>
        {
            var current = await s.GetCarrierAsync(id, ct);
            var carrier = await s.UpdateCarrierAsync(id, new CarrierDetails(
                r.Name, r.DepositAccountId, ApiKeep.Id(r.VendorId, current?.VendorId), ApiKeep.Id(r.DueAccountId, current?.DueAccountId),
                ApiKeep.Id(r.ClassId, current?.ClassId), ApiKeep.Id(r.LocationId, current?.LocationId), r.Unit ?? current?.Unit), ct);
            return CarrierResponse.From(r.IsActive is { } active ? await s.SetCarrierActiveAsync(id, active, ct) : carrier);
        });
        settlements.MapPut("/carriers/{id:guid}/codes", async (Guid id, CarrierCodesRequest r, SettlementService s, CancellationToken ct) =>
            CarrierResponse.From(await s.SetCodesAsync(id, (r.Codes ?? []).Select(c => new CarrierCodeDraft(c.Section, c.Code, c.AccountId, c.Name, c.ClassId)).ToList(),
                (r.Forget ?? []).Select(c => (c.Section, c.Code)).ToList(), ct)));

        settlements.MapGet("", async (Guid? carrierId, SettlementService s, CancellationToken ct) => (await s.ListAsync(carrierId, ct)).Select(x => SettlementResponse.From(x)));
        settlements.MapPost("", async (SettlementRequest r, SettlementService s, CancellationToken ct) =>
        {
            var settlement = await s.CreateAsync(r.CarrierId ?? Guid.Empty, r.ToDetails(), ct);
            return Results.Created($"/api/settlements/{settlement.Id}", SettlementResponse.From(settlement, lines: true));
        });
        settlements.MapGet("/{id:guid}", async (Guid id, SettlementService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } x ? Results.Ok(SettlementResponse.From(x, lines: true)) : Results.NotFound());
        settlements.MapPut("/{id:guid}", async (Guid id, SettlementRequest r, SettlementService s, CancellationToken ct) =>
            SettlementResponse.From(await s.ChangeAsync(id, r.ToDetails(), ct), lines: true));
        // A CSV file as the body; ?date= is the pay date of settlements the file gives none for, ?dryRun=true shows what's in it.
        settlements.MapPost("/import", async (HttpRequest request, Guid carrierId, DateOnly? date, bool? dryRun, SettlementService s, CancellationToken ct) =>
        {
            using var memory = new MemoryStream();
            await request.Body.CopyToAsync(memory, ct);
            return await s.ImportAsync(carrierId, memory.ToArray(), date ?? OpenBook.Application.CompanyTime.Today, save: dryRun != true, ct);
        });
        // A statement (PDF or picture) as the body; only the pages given (1-4, 7) are read, sent and kept. ?with=claude or thisComputer.
        settlements.MapPost("/read", async (HttpRequest request, Guid carrierId, string? pages, string? fileName, SettlementReadWith? with, DateOnly? date, SettlementCaptureService s, CancellationToken ct) =>
        {
            using var memory = new MemoryStream();
            await request.Body.CopyToAsync(memory, ct);
            var settlement = await s.ReadAsync(carrierId, fileName ?? "statement.pdf", memory.ToArray(), SettlementCaptureService.ParsePages(pages), with ?? SettlementReadWith.Claude,
                date ?? OpenBook.Application.CompanyTime.Today, ct);
            return Results.Created($"/api/settlements/{settlement.Id}", SettlementResponse.From(settlement, lines: true));
        });
        settlements.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmSettlementRequest r, SettlementService s, CancellationToken ct) =>
            SettlementResponse.From(await s.ConfirmAsync(id, r.NetPaid, r.CheckedLines ?? [], r.AllChecked, ct), lines: true));
        settlements.MapGet("/{id:guid}/review", async (Guid id, SettlementService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } x ? Results.Ok(await s.ReviewAsync(x, ct)) : Results.NotFound());
        settlements.MapGet("/{id:guid}/unassigned", async (Guid id, SettlementService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } x ? Results.Ok(await s.UnassignedAsync(x, ct)) : Results.NotFound());
        settlements.MapPost("/{id:guid}/post", async (Guid id, SettlementService s, CancellationToken ct) => SettlementResponse.From(await s.PostAsync(id, ct)));
        settlements.MapPost("/{id:guid}/void", async (Guid id, VoidSettlementRequest r, SettlementService s, CancellationToken ct) =>
            SettlementResponse.From(await s.VoidAsync(id, r.Reason ?? "Settlement voided", ct)));
        settlements.MapPost("/{id:guid}/dismiss", async (Guid id, SettlementService s, CancellationToken ct) => SettlementResponse.From(await s.SetDismissedAsync(id, true, ct)));
        settlements.MapPost("/{id:guid}/restore", async (Guid id, SettlementService s, CancellationToken ct) => SettlementResponse.From(await s.SetDismissedAsync(id, false, ct)));
    }
}
