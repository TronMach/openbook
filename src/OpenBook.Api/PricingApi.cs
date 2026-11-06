using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Pricing;
using OpenBook.Application;
using OpenBook.Domain.Pricing;

namespace OpenBook.Api;

public sealed record QuantityBreakDto(decimal FromQuantity, QuantityBreakKind Kind, decimal Value);

public sealed record LevelItemPriceDto(Guid ItemId, decimal Price);

public sealed record PriceLevelResponse(Guid Id, string Name, PriceLevelKind Kind, decimal Percent, bool IsActive, IReadOnlyList<LevelItemPriceDto> Items)
{
    public static PriceLevelResponse From(PriceLevel l) =>
        new(l.Id, l.Name, l.Kind, l.Percent, l.IsActive, l.Items.Select(i => new LevelItemPriceDto(i.ItemId, i.Price)).ToList());
}

public sealed record PriceRuleResponse(
    Guid Id, string Name, PriceAdjustment Adjustment, decimal Value, IReadOnlyList<Guid> Items, RuleCustomers Customers, IReadOnlyList<Guid> Targets,
    DateOnly? StartsOn, DateOnly? EndsOn, decimal? MinQuantity, decimal? MaxQuantity, int Priority, bool IsExclusive, PriceRounding? Rounding, bool IsActive)
{
    public static PriceRuleResponse From(PriceRule r) => new(
        r.Id, r.Name, r.Adjustment, r.Value, r.TargetIds(RuleTargetKind.Item), r.Customers,
        r.Targets.Where(t => t.Kind != RuleTargetKind.Item).Select(t => t.TargetId).ToList(),
        r.StartsOn, r.EndsOn, r.MinQuantity, r.MaxQuantity, r.Priority, r.IsExclusive, r.Rounding, r.IsActive);
}

public sealed record PriceQuoteResponse(decimal ListPrice, decimal Price, string? Source, IReadOnlyList<string> Steps);

public sealed record SetRoundingRequest(PriceRounding Rounding);

internal static class PricingApi
{
    public static void MapPricingApi(this RouteGroupBuilder api)
    {
        var pricing = api.MapGroup("/pricing");

        pricing.MapGet("/levels", async (PricingService s, CancellationToken ct) => (await s.ListLevelsAsync(true, ct)).Select(PriceLevelResponse.From));
        pricing.MapGet("/levels/{id:guid}", async (Guid id, PricingService s, CancellationToken ct) =>
            await s.GetLevelAsync(id, ct) is { } l ? Results.Ok(PriceLevelResponse.From(l)) : Results.NotFound());
        pricing.MapPost("/levels", async (PriceLevelDetails r, PricingService s, CancellationToken ct) =>
        {
            var l = await s.CreateLevelAsync(r, ct);
            return Results.Created($"/api/pricing/levels/{l.Id}", PriceLevelResponse.From(l));
        });
        pricing.MapPut("/levels/{id:guid}", async (Guid id, PriceLevelDetails r, PricingService s, CancellationToken ct) =>
            PriceLevelResponse.From(await s.UpdateLevelAsync(id, r, ct)));
        pricing.MapPut("/levels/{id:guid}/active", async (Guid id, SetActiveRequest r, PricingService s, CancellationToken ct) =>
            PriceLevelResponse.From(await s.SetLevelActiveAsync(id, r.Active, ct)));
        pricing.MapPut("/levels/{id:guid}/items", async (Guid id, IReadOnlyList<LevelItemPriceDto> r, PricingService s, CancellationToken ct) =>
            PriceLevelResponse.From(await s.SetLevelPricesAsync(id, r.Select(p => (p.ItemId, p.Price)).ToList(), ct: ct)));
        pricing.MapPost("/levels/{id:guid}/import", async (Guid id, HttpRequest request, PricingService s, CancellationToken ct) =>
        {
            var result = await s.ImportLevelPricesAsync(id, await OpenBook.Application.Import.TextFiles.ReadAsync(request.Body, ct), ct);
            return result.Errors.Count == 0 ? Results.Ok(result) : Results.BadRequest(result);
        }).Accepts<string>("text/csv");
        pricing.MapGet("/levels/{id:guid}/export", async (Guid id, DateOnly? date, PricingService s, CancellationToken ct) =>
            Results.Text(await s.ExportLevelPricesAsync(id, date ?? CompanyTime.Today, ct), "text/csv"));

        pricing.MapGet("/rules", async (PricingService s, CancellationToken ct) => (await s.ListRulesAsync(ct)).Select(PriceRuleResponse.From));
        pricing.MapGet("/rules/{id:guid}", async (Guid id, PricingService s, CancellationToken ct) =>
            await s.GetRuleAsync(id, ct) is { } r ? Results.Ok(PriceRuleResponse.From(r)) : Results.NotFound());
        pricing.MapPost("/rules", async (PriceRuleDetails r, PricingService s, CancellationToken ct) =>
        {
            var rule = await s.CreateRuleAsync(r, ct);
            return Results.Created($"/api/pricing/rules/{rule.Id}", PriceRuleResponse.From(rule));
        });
        pricing.MapPut("/rules/{id:guid}", async (Guid id, PriceRuleDetails r, PricingService s, CancellationToken ct) =>
            PriceRuleResponse.From(await s.UpdateRuleAsync(id, r, ct)));
        pricing.MapPut("/rules/{id:guid}/active", async (Guid id, SetActiveRequest r, PricingService s, CancellationToken ct) =>
            PriceRuleResponse.From(await s.SetRuleActiveAsync(id, r.Active, ct)));

        pricing.MapPut("/items/{id:guid}/quantity-breaks", async (Guid id, IReadOnlyList<QuantityBreakDto> r, PricingService s, CancellationToken ct) =>
            ItemResponse.From(await s.SetQuantityBreaksAsync(id, r.Select(b => (b.FromQuantity, b.Kind, b.Value)).ToList(), ct)));

        pricing.MapGet("/rounding", async (PricingService s, CancellationToken ct) => new SetRoundingRequest(await s.GetRoundingAsync(ct)));
        pricing.MapPut("/rounding", async (SetRoundingRequest r, PricingService s, CancellationToken ct) =>
        {
            await s.SetRoundingAsync(r.Rounding, ct);
            return Results.NoContent();
        });

        pricing.MapGet("/quote", async (Guid itemId, Guid? customerId, decimal? quantity, DateOnly? date, PricingService s, CancellationToken ct) =>
        {
            var q = await s.QuoteAsync(customerId, itemId, quantity ?? 1m, date ?? CompanyTime.Today, ct);
            return new PriceQuoteResponse(q.ListPrice, q.Price, q.Source, q.Steps);
        });
        pricing.MapGet("/last-prices/{customerId:guid}", async (Guid customerId, PricingService s, CancellationToken ct) =>
            (await s.LastPricesAsync(customerId, ct)).Select(p => new { ItemId = p.Key, p.Value.Date, p.Value.Rate, p.Value.DocumentNumber }));
    }
}
