using OpenBook.Application.Segments;
using OpenBook.Domain.Segments;

namespace OpenBook.Web.Components.Segments;

/// <summary>
/// What transaction forms offer: active classes when the company tracks classes, active locations when it tracks
/// locations, and what locations are called. Empty lists hide the fields.
/// </summary>
public sealed record SegmentOptions(IReadOnlyList<SegmentChoice> Classes, IReadOnlyList<SegmentChoice> Locations, string LocationLabel)
{
    public static SegmentOptions None { get; } = new([], [], Domain.Companies.Company.DefaultLocationLabel);

    /// <summary>For line editors' <c>Classes</c> parameter: null hides the class column.</summary>
    public IReadOnlyList<SegmentChoice>? ClassColumn => Classes.Count > 0 ? Classes : null;

    public static async Task<SegmentOptions> LoadAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var segments = services.GetRequiredService<SegmentService>();
        var tracking = await segments.GetTrackingAsync();
        return new SegmentOptions(
            tracking.Classes ? await segments.ListAsync(SegmentKind.Class, includeInactive: false) : [],
            tracking.Locations ? await segments.ListAsync(SegmentKind.Location, includeInactive: false) : [],
            tracking.LocationLabel);
    }
}
