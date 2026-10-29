using OpenBook.Application.Channels;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts for sales channels and their payouts.</summary>
public static class ChannelEndpoints
{
    public const long MaxReportBytes = 20L * 1024 * 1024;

    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/sales/channels", async (IFormCollection form, ChannelService channels, CancellationToken ct) =>
        {
            try
            {
                var channel = await channels.CreateAsync(Details(form), ct);
                return Results.Redirect($"/sales/channels/{channel.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/sales/channels?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/sales/channels/{id:guid}", async (Guid id, IFormCollection form, ChannelService channels, CancellationToken ct) =>
            await Back($"/sales/channels/{id}", async () =>
            {
                await channels.UpdateAsync(id, Details(form), ct);
                await channels.SetActiveAsync(id, form["active"] == "true", ct);
            })).RequireAuthorization();

        // Form-bound (the file), so the page's antiforgery token is checked automatically.
        app.MapPost("/sales/channels/{id:guid}/import", async (Guid id, IFormFile? file, ChannelService channels, CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.Redirect($"/sales/channels/{id}?error={Uri.EscapeDataString("Choose the report file to import.")}");
            if (file.Length > MaxReportBytes)
                return Results.Redirect($"/sales/channels/{id}?error={Uri.EscapeDataString("The file is larger than 20 MB. Download a shorter period.")}");
            using var memory = new MemoryStream();
            await file.CopyToAsync(memory, ct);
            try
            {
                var result = await channels.ImportAsync(id, file.FileName, memory.ToArray(), save: true, ct);
                var query = $"imported={result.Saved.Count}&skipped={result.AlreadyImported.Count}";
                if (result.Warnings.Count > 0)
                    query += $"&note={Uri.EscapeDataString(string.Join(" ", result.Warnings))}";
                return Results.Redirect($"/sales/channels/{id}?{query}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/sales/channels/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxReportBytes));

        app.MapPost("/sales/channels/connections/{id:guid}", async (Guid id, IFormCollection form, ChannelSyncService sync, CancellationToken ct) =>
            await Back($"/sales/channels/{id}", () => sync.ConnectAsync(id, form["shopDomain"].ToString(), form["credential"].ToString(), ct))).RequireAuthorization();

        app.MapPost("/sales/channels/connections/{id:guid}/disconnect", async (Guid id, ChannelSyncService sync, CancellationToken ct) =>
            await Back($"/sales/channels/{id}", () => sync.DisconnectAsync(id, ct))).RequireAuthorization();

        app.MapPost("/sales/channels/connections/{id:guid}/sync", async (Guid id, ChannelSyncService sync, CancellationToken ct) =>
        {
            try
            {
                var result = await sync.SyncAsync(id, ct);
                return Results.Redirect(result.Succeeded ? $"/sales/channels/{id}?imported={result.Imported}" : $"/sales/channels/{id}?error={Uri.EscapeDataString(result.Error!)}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/sales/channels/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/sales/channels/payouts/{id:guid}/post", async (Guid id, ChannelService channels, CancellationToken ct) =>
            await Back($"/sales/channels/payouts/{id}", () => channels.PostAsync(id, ct))).RequireAuthorization();

        app.MapPost("/sales/channels/payouts/{id:guid}/void", async (Guid id, IFormCollection form, ChannelService channels, CancellationToken ct) =>
            await Back($"/sales/channels/payouts/{id}", () => channels.VoidAsync(id, form["reason"].ToString() is { Length: > 0 } r ? r : "Payout voided", ct)))
            .RequireAuthorization();

        app.MapPost("/sales/channels/payouts/{id:guid}/dismiss", async (Guid id, IFormCollection form, ChannelService channels, CancellationToken ct) =>
            await Back($"/sales/channels/payouts/{id}", () => channels.SetDismissedAsync(id, form["restore"] != "true", ct))).RequireAuthorization();

        return app;
    }

    private static ChannelDetails Details(IFormCollection form)
    {
        Guid Id(string name) => Guid.TryParse(form[name], out var id) ? id : Guid.Empty;
        return new ChannelDetails(
            form["name"].ToString(),
            Enum.TryParse<ChannelKind>(form["kind"], out var kind) ? kind : ChannelKind.Other,
            Id("depositAccountId"),
            Id("salesAccountId"),
            Id("shippingAccountId"),
            Id("refundsAccountId"),
            Id("feesAccountId"),
            Id("taxAccountId"),
            Id("adjustmentsAccountId"),
            Id("clearingAccountId") is var clearing && clearing != Guid.Empty ? clearing : null);
    }

    private static async Task<IResult> Back(string page, Func<Task> change)
    {
        try
        {
            await change();
            return Results.Redirect(page);
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}");
        }
    }
}
