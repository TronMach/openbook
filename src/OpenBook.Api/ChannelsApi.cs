using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Channels;
using OpenBook.Domain.Channels;

namespace OpenBook.Api;

public sealed record PayoutResponse(
    Guid Id, Guid ChannelId, string ExternalId, DateOnly Date, DateOnly? PeriodStart, DateOnly? PeriodEnd, PayoutStatus Status, decimal Net, decimal? ReportedNet,
    Guid? JournalEntryId, IReadOnlyDictionary<ChannelLineType, decimal> Totals, IReadOnlyList<PayoutLineResponse>? Lines)
{
    public static PayoutResponse From(ChannelPayout p, bool lines = false) => new(
        p.Id, p.ChannelId, p.ExternalId, p.Date, p.PeriodStart, p.PeriodEnd, p.Status, p.Net, p.ReportedNet, p.JournalEntryId,
        p.Lines.GroupBy(l => l.Type).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount)),
        lines ? p.Lines.Select(l => new PayoutLineResponse(l.Date, l.Type, l.Amount, l.Reference, l.Description, l.Region)).ToList() : null);
}

public sealed record PayoutLineResponse(DateOnly Date, ChannelLineType Type, decimal Amount, string? Reference, string? Description, string? Region);

public sealed record PayoutImportResponse(IReadOnlyList<string> New, IReadOnlyList<string> AlreadyImported, IReadOnlyList<string> Warnings, IReadOnlyList<PayoutResponse> Saved);

public sealed record VoidPayoutRequest(string? Reason);

public static class ChannelsApi
{
    public static void MapChannelsApi(this RouteGroupBuilder api)
    {
        var channels = api.MapGroup("/channels");
        channels.MapGet("", async (ChannelService s, CancellationToken ct) => await s.ListAsync(includeInactive: true, ct));
        channels.MapPost("", async (ChannelDetails r, ChannelService s, CancellationToken ct) =>
        {
            var channel = await s.CreateAsync(r, ct);
            return Results.Created($"/api/channels/{channel.Id}", channel);
        });
        channels.MapPut("/{id:guid}", async (Guid id, ChannelDetails r, ChannelService s, CancellationToken ct) => await s.UpdateAsync(id, r, ct));
        channels.MapGet("/{id:guid}/payouts", async (Guid id, ChannelService s, CancellationToken ct) =>
            (await s.PayoutsAsync(id, ct)).Select(p => PayoutResponse.From(p)));

        // The report file as the body; ?fileName= for the record, ?dryRun=true to see what's new without saving.
        channels.MapPost("/{id:guid}/import", async (Guid id, HttpRequest request, string? fileName, bool? dryRun, ChannelService s, CancellationToken ct) =>
        {
            using var memory = new MemoryStream();
            await request.Body.CopyToAsync(memory, ct);
            var result = await s.ImportAsync(id, fileName ?? "report.csv", memory.ToArray(), save: dryRun != true, ct);
            return new PayoutImportResponse(
                result.New.Select(p => p.ExternalId).ToList(), result.AlreadyImported.Select(p => p.ExternalId).ToList(), result.Warnings,
                result.Saved.Select(p => PayoutResponse.From(p)).ToList());
        });

        channels.MapGet("/payouts/{id:guid}", async (Guid id, ChannelService s, CancellationToken ct) =>
            await s.GetPayoutAsync(id, ct) is { } p ? Results.Ok(PayoutResponse.From(p, lines: true)) : Results.NotFound());
        channels.MapPost("/payouts/{id:guid}/post", async (Guid id, ChannelService s, CancellationToken ct) => PayoutResponse.From(await s.PostAsync(id, ct)));
        channels.MapPost("/payouts/{id:guid}/void", async (Guid id, VoidPayoutRequest r, ChannelService s, CancellationToken ct) =>
            PayoutResponse.From(await s.VoidAsync(id, r.Reason ?? "Payout voided", ct)));
        channels.MapPost("/payouts/{id:guid}/dismiss", async (Guid id, ChannelService s, CancellationToken ct) => PayoutResponse.From(await s.SetDismissedAsync(id, true, ct)));
        channels.MapPost("/payouts/{id:guid}/restore", async (Guid id, ChannelService s, CancellationToken ct) => PayoutResponse.From(await s.SetDismissedAsync(id, false, ct)));
    }
}
