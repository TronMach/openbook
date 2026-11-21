using OpenBook.Application;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Email;
using OpenBook.Web.Components.Emails;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Text messages: the live preview beside the compose page (posted by texts.js), the Twilio settings and their test, and a
/// customer's mobile number and agreement. Sending itself is the compose page's own form.
/// </summary>
public static class TextEndpoints
{
    public static IEndpointRouteBuilder MapTextEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: Input.Body (or body). JSON back: the text as it will arrive, its length and what stops it.
        app.MapPost("/sales/text/{kind}/{id:guid}/preview", async (string kind, Guid id, IFormCollection form, TextMessageService texts, CancellationToken ct) =>
        {
            if (TextKinds.FromRoute(kind) is not { } k)
                return Results.NotFound();
            try
            {
                var body = form.TryGetValue("Input.Body", out var value) || form.TryGetValue("body", out value) ? value.ToString() : "";
                var p = await texts.PreviewAsync(new EmailSource(k, id), body, ct);
                return Results.Json(new
                {
                    text = p.Text, characters = p.Length.Characters, parts = p.Length.Parts, unicode = p.Length.Unicode, problems = p.Problems, notes = p.Notes,
                });
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // Fields: accountSid, authToken, clearAuthToken, sender.
        app.MapPost("/settings/texts", async (IFormCollection form, IRecordVersions versions, TextMessageService texts, CancellationToken ct) =>
            await Back("/settings/texts", async () =>
            {
                await Components.SettingsVersions.EnsureUnchangedAsync<TextSettings>(versions, form, ct);
                await texts.SaveSettingsAsync(new TextSettingsCommand(form["accountSid"], form["authToken"], form["sender"], form["clearAuthToken"] == "true"), ct);
                return "done=saved";
            })).RequireAuthorization();

        // Fields: to. The number stays out of the address it comes back to.
        app.MapPost("/settings/texts/test", async (IFormCollection form, TextMessageService texts, CancellationToken ct) =>
            await Back("/settings/texts", async () =>
            {
                await texts.SendTestAsync(form["to"], ct);
                return "done=tested";
            })).RequireAuthorization();

        // Fields: number, allowed.
        app.MapPost("/customers/{id:guid}/text-settings", async (Guid id, IFormCollection form, TextMessageService texts, CancellationToken ct) =>
        {
            try
            {
                await texts.SetCustomerAsync(id, form["number"].ToString(), form["allowed"] == "true", ct);
                return Results.Redirect($"/customers/{id}#text-settings");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/customers/{id}?textError={Uri.EscapeDataString(ex.Message)}#text-settings");
            }
        }).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Back(string page, Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"{page}?{await change()}");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}");
        }
    }
}
