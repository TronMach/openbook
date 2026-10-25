using OpenBook.Application.Segments;
using OpenBook.Domain.Common;
using OpenBook.Domain.Segments;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Form posts from the segment pages: adding a class, location or tag, the tracking settings, an entry's tags and
/// reclassifying lines. Each binds the whole form, so the antiforgery token is validated automatically.
/// </summary>
public static class SegmentEndpoints
{
    public static IEndpointRouteBuilder MapSegmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/segments", async (IFormCollection form, SegmentService service, CancellationToken ct) =>
        {
            if (!Enum.TryParse<SegmentKind>(form["kind"], out var kind))
                return Back("/segments", "Choose what to add.");
            var parentId = Guid.TryParse(form["parentId"], out var p) ? p : (Guid?)null;
            return await Run("/segments", () => service.CreateAsync(kind, form["name"].ToString(), parentId, ct));
        }).RequireAuthorization();

        app.MapPost("/segments/tracking", async (IFormCollection form, SegmentService service, CancellationToken ct) =>
            await Run("/segments", () => service.SetTrackingAsync(
                new SegmentTracking(form["classes"] == "true", form["locations"] == "true", form["label"].ToString()), ct))).RequireAuthorization();

        // Fields: entryId, tagId (one per checked tag), back (the page to return to).
        app.MapPost("/segments/tags", async (IFormCollection form, SegmentService service, CancellationToken ct) =>
        {
            var back = LocalPath(form["back"].ToString());
            if (!Guid.TryParse(form["entryId"], out var entryId))
                return Back(back, "Unknown transaction.");
            var tags = form["tagId"].Select(t => Guid.TryParse(t, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
            return await Run(back, () => service.SetTagsAsync(entryId, tags, ct));
        }).RequireAuthorization();

        // Fields: line (one per selected line), setClass / setLocation ("" leaves it, "none" clears it, else an id), back.
        app.MapPost("/segments/reclassify", async (IFormCollection form, SegmentService service, CancellationToken ct) =>
        {
            var back = LocalPath(form["back"].ToString());
            var lines = form["line"].Select(l => Guid.TryParse(l, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
            var (setClass, classId) = Choice(form["setClass"].ToString());
            var (setLocation, locationId) = Choice(form["setLocation"].ToString());
            try
            {
                var changed = await service.ReclassifyAsync(new ReclassifyCommand(lines, setClass, classId, setLocation, locationId), ct);
                return Results.Redirect($"{back}{(back.Contains('?', StringComparison.Ordinal) ? '&' : '?')}done={changed}");
            }
            catch (DomainException ex)
            {
                return Back(back, ex.Message);
            }
        }).RequireAuthorization();

        return app;
    }

    private static (bool Set, Guid? Id) Choice(string value) =>
        value.Length == 0 ? (false, null) : value == "none" ? (true, null) : (true, Guid.TryParse(value, out var id) ? id : null);

    /// <summary>Only paths on this site, so a form can't redirect elsewhere.</summary>
    private static string LocalPath(string back) =>
        back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) && !back.StartsWith("/\\", StringComparison.Ordinal) ? back
        : back.Length > 0 && !back.Contains(':', StringComparison.Ordinal) && !back.StartsWith('/') ? "/" + back
        : "/";

    private static async Task<IResult> Run<T>(string back, Func<Task<T>> action)
    {
        try
        {
            await action();
            return Results.Redirect(back);
        }
        catch (DomainException ex)
        {
            return Back(back, ex.Message);
        }
    }

    private static IResult Back(string path, string error) =>
        Results.Redirect($"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}error={Uri.EscapeDataString(error)}");
}
