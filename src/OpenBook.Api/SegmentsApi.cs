using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Segments;
using OpenBook.Domain.Segments;

namespace OpenBook.Api;

public sealed record SegmentResponse(Guid Id, SegmentKind Kind, string Name, string FullName, Guid? ParentId, bool IsActive)
{
    public static SegmentResponse From(SegmentChoice c) =>
        new(c.Segment.Id, c.Segment.Kind, c.Segment.Name, c.FullName, c.Segment.ParentId, c.Segment.IsActive);
}

public sealed record CreateSegmentRequest(SegmentKind Kind, string Name, Guid? ParentId = null);

public sealed record UpdateSegmentRequest(string Name, Guid? ParentId = null);

public sealed record SetTagsRequest(IReadOnlyList<Guid> TagIds);

internal static class SegmentsApi
{
    public static void MapSegmentsApi(this RouteGroupBuilder api)
    {
        var segments = api.MapGroup("/segments");

        segments.MapGet("", async (SegmentKind? kind, bool? includeInactive, SegmentService s, CancellationToken ct) =>
        {
            var kinds = kind is { } k ? [k] : Enum.GetValues<SegmentKind>();
            var result = new List<SegmentResponse>();
            foreach (var each in kinds)
                result.AddRange((await s.ListAsync(each, includeInactive ?? true, ct)).Select(SegmentResponse.From));
            return result;
        });
        segments.MapPost("", async (CreateSegmentRequest r, SegmentService s, CancellationToken ct) =>
        {
            var segment = await s.CreateAsync(r.Kind, r.Name, r.ParentId, ct);
            var names = await s.NamesAsync(ct);
            return Results.Created($"/api/segments/{segment.Id}", new SegmentResponse(segment.Id, segment.Kind, segment.Name, names[segment.Id], segment.ParentId, segment.IsActive));
        });
        segments.MapPut("/{id:guid}", async (Guid id, UpdateSegmentRequest r, SegmentService s, CancellationToken ct) =>
        {
            var segment = await s.UpdateAsync(id, r.Name, r.ParentId, ct);
            return new SegmentResponse(segment.Id, segment.Kind, segment.Name, (await s.NamesAsync(ct))[id], segment.ParentId, segment.IsActive);
        });
        segments.MapPut("/{id:guid}/active", async (Guid id, SetActiveRequest r, SegmentService s, CancellationToken ct) =>
        {
            var segment = await s.SetActiveAsync(id, r.Active, ct);
            return new SegmentResponse(segment.Id, segment.Kind, segment.Name, (await s.NamesAsync(ct))[id], segment.ParentId, segment.IsActive);
        });

        segments.MapGet("/tracking", (SegmentService s, CancellationToken ct) => s.GetTrackingAsync(ct));
        segments.MapPut("/tracking", (SegmentTracking r, SegmentService s, CancellationToken ct) => s.SetTrackingAsync(r, ct));

        // Tags belong to the journal entry a transaction posted (its journalEntryId).
        segments.MapGet("/tags/{entryId:guid}", (Guid entryId, SegmentService s, CancellationToken ct) => s.GetTagsAsync(entryId, ct));
        segments.MapPut("/tags/{entryId:guid}", (Guid entryId, SetTagsRequest r, SegmentService s, CancellationToken ct) => s.SetTagsAsync(entryId, r.TagIds, ct));

        segments.MapPost("/reclassify", async (ReclassifyCommand r, SegmentService s, CancellationToken ct) =>
            new { Changed = await s.ReclassifyAsync(r, ct) });
    }
}
