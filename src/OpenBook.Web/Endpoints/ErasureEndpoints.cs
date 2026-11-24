using OpenBook.Application;
using OpenBook.Application.Lists;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Web.Components;

namespace OpenBook.Web.Endpoints;

/// <summary>"Erase personal details" of a customer or vendor (antiforgery-checked, form-bound; admins).</summary>
public static class ErasureEndpoints
{
    public static IEndpointRouteBuilder MapErasureEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: confirm (the name, typed), the record's version.
        app.MapPost("/erase/{kind:regex(^(customers|vendors)$)}/{id:guid}/confirm", async (
            string kind, Guid id, IFormCollection form, ErasureService erasure, IRecordVersions versions, CancellationToken ct) =>
        {
            var confirmation = form["confirm"].ToString();
            try
            {
                if (kind == "vendors")
                {
                    await SettingsVersions.EnsureUnchangedAsync<Vendor>(versions, id, form, ct);
                    await erasure.EraseVendorAsync(id, confirmation, ct);
                }
                else
                {
                    await SettingsVersions.EnsureUnchangedAsync<Customer>(versions, id, form, ct);
                    await erasure.EraseCustomerAsync(id, confirmation, ct);
                }

                return Results.Redirect($"/{kind}/{id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/erase/{kind}/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        return app;
    }
}
