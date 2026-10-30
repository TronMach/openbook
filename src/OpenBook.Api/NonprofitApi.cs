using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Nonprofit;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;

namespace OpenBook.Api;

public sealed record ReleaseResponse(Guid Id, DateOnly Date, Guid? ClassId, decimal Amount, string? Memo, DocumentStatus Status, Guid JournalEntryId)
{
    public static ReleaseResponse From(RestrictionRelease r) => new(r.Id, r.Date, r.ClassId, r.Amount, r.Memo, r.Status, r.JournalEntryId);
}

internal static class NonprofitApi
{
    public static void MapNonprofitApi(this RouteGroupBuilder api)
    {
        var nonprofit = api.MapGroup("/nonprofit");

        nonprofit.MapGet("/purposes", (DateOnly? asOf, NonprofitService s, CancellationToken ct) =>
            s.PurposesAsync(asOf ?? DateOnly.FromDateTime(DateTime.Today), ct));
        nonprofit.MapGet("/releases", async (NonprofitService s, CancellationToken ct) => (await s.ListReleasesAsync(ct)).Select(ReleaseResponse.From));
        nonprofit.MapGet("/releases/{id:guid}", async (Guid id, NonprofitService s, CancellationToken ct) =>
            await s.GetReleaseAsync(id, ct) is { } r ? Results.Ok(ReleaseResponse.From(r)) : Results.NotFound());
        nonprofit.MapPost("/releases", async (ReleaseCommand r, NonprofitService s, CancellationToken ct) =>
        {
            var release = await s.ReleaseAsync(r, ct);
            return Results.Created($"/api/nonprofit/releases/{release.Id}", ReleaseResponse.From(release));
        });
        nonprofit.MapPost("/releases/{id:guid}/void", async (Guid id, VoidRequest r, NonprofitService s, CancellationToken ct) =>
            ReleaseResponse.From(await s.VoidAsync(id, r.Reason, ct)));
        nonprofit.MapPost("/functional-classes", async (NonprofitService s, CancellationToken ct) =>
            new { Changed = await s.SetUpFunctionalClassesAsync(ct) });
    }
}
