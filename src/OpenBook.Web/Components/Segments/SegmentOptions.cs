using OpenBook.Application.Segments;
using OpenBook.Domain.Segments;

namespace OpenBook.Web.Components.Segments;

/// <summary>
/// What transaction forms offer: active classes when the company tracks classes, active locations when it tracks
/// locations, what locations are called, and active tags (flat, always offered). Empty lists hide the fields.
/// </summary>
public sealed record SegmentOptions(IReadOnlyList<SegmentChoice> Classes, IReadOnlyList<SegmentChoice> Locations, string LocationLabel, IReadOnlyList<SegmentChoice> Tags)
{
    public static SegmentOptions None { get; } = new([], [], Domain.Companies.Company.DefaultLocationLabel, []);

    /// <summary>For line editors' <c>Classes</c> parameter: null hides the class column.</summary>
    public IReadOnlyList<SegmentChoice>? ClassColumn => Classes.Count > 0 ? Classes : null;

    public static Task<SegmentOptions> LoadAsync(IServiceProvider services) => LoadAsync(services, []);

    /// <param name="services">The scope's services.</param>
    /// <param name="onDocument">What a document being corrected already has: offered though it's been deactivated since.</param>
    public static async Task<SegmentOptions> LoadAsync(IServiceProvider services, IEnumerable<Guid?> onDocument)
    {
        ArgumentNullException.ThrowIfNull(services);
        var kept = onDocument.OfType<Guid>().ToHashSet();
        var segments = services.GetRequiredService<SegmentService>();
        var tracking = await segments.GetTrackingAsync();
        async Task<IReadOnlyList<SegmentChoice>> ChoicesAsync(SegmentKind kind) =>
            (await segments.ListAsync(kind, includeInactive: kept.Count > 0)).Where(c => c.Segment.IsActive || kept.Contains(c.Segment.Id)).ToList();
        return new SegmentOptions(
            tracking.Classes ? await ChoicesAsync(SegmentKind.Class) : [],
            tracking.Locations ? await ChoicesAsync(SegmentKind.Location) : [],
            tracking.LocationLabel,
            await ChoicesAsync(SegmentKind.Tag));
    }
}
