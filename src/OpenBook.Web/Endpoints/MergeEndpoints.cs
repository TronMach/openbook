using OpenBook.Application.Lists;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Endpoints;

/// <summary>"Merge into another" on customer, vendor and item pages (antiforgery-checked, form-bound; accountant role).</summary>
public static class MergeEndpoints
{
    public static IEndpointRouteBuilder MapMergeEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: from, into, confirm.
        app.MapPost("/merge/{kind:regex(^(customers|vendors|items)$)}", async (string kind, IFormCollection form, MergeService merge, CancellationToken ct) =>
        {
            var from = Guid.TryParse(form["from"], out var f) ? f : Guid.Empty;
            var page = kind == "items" ? "/items" : $"/{kind}";
            try
            {
                if (!Guid.TryParse(form["into"], out var into))
                    throw new DomainException("merge.into_required", "Choose the record to merge it into.");
                if (form["confirm"] != "true")
                    throw new DomainException("merge.confirm_required", "Tick the box to confirm the merge.");
                var result = kind switch
                {
                    "customers" => await merge.MergeCustomersAsync(from, into, ct),
                    "vendors" => await merge.MergeVendorsAsync(from, into, ct),
                    _ => await merge.MergeItemsAsync(from, into, ct),
                };
                return Results.Redirect($"{page}/{result.KeptId}?merged={Uri.EscapeDataString(result.MergedName)}#merge");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{page}/{from}?mergeError={Uri.EscapeDataString(ex.Message)}#merge");
            }
        }).RequireAuthorization();

        return app;
    }
}
