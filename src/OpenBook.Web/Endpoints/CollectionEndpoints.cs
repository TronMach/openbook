using System.Globalization;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the collections, collection settings and write-off pages (antiforgery-checked, form-bound).</summary>
public static class CollectionEndpoints
{
    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: invoice (one per ticked row), date.
        app.MapPost("/sales/collections/late-fees", async (IFormCollection form, LateFeeService fees, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back("/sales/collections", "#late-fees", async () =>
            {
                var created = await fees.ChargeAsync(Date(form["date"]) ?? Today(), Chosen(form), ct);
                return $"done=fees&count={created.Count}";
            });
        }).RequireAuthorization();

        app.MapPost("/sales/collections/reminders", async (IFormCollection form, ReminderService reminders, CancellationToken ct) =>
            await Back("/sales/collections", "#reminders", async () =>
            {
                var results = await reminders.SendAsync(Today(), Chosen(form), ct);
                var failed = results.Where(r => r.Error is not null).ToList();
                if (failed.Count > 0)
                    throw new DomainException("reminder.send_failed",
                        $"{results.Count - failed.Count} sent; {failed.Count} not sent ({failed[0].Error}).");
                return $"done=reminders&count={results.Count}";
            })).RequireAuthorization();

        // Fields: back (the page to return to).
        app.MapPost("/sales/scheduled-emails/{id:guid}/cancel", async (Guid id, IFormCollection form, ScheduledEmailService scheduled, CancellationToken ct) =>
            await Back(ScheduledBack(form), "#scheduled-emails", async () =>
            {
                await scheduled.CancelAsync(id, ct);
                return "done=cancelled";
            })).RequireAuthorization();

        app.MapPost("/sales/scheduled-emails/{id:guid}/retry", async (Guid id, IFormCollection form, ScheduledEmailService scheduled, CancellationToken ct) =>
            await Back(ScheduledBack(form), "#scheduled-emails", async () =>
            {
                await scheduled.RetryAsync(id, ct);
                return "done=retry";
            })).RequireAuthorization();

        app.MapPost("/sales/collections/settings/late-fees", async (IFormCollection form, CollectionService collections, CancellationToken ct) =>
            await Back("/sales/collections/settings", "#late-fees", async () =>
            {
                await collections.SaveLateFeesAsync(new LateFeeSettingsCommand(
                    form["enabled"] == "true", form["automatic"] == "true", Amount(form["percent"]) ?? 0m, Amount(form["amount"]) ?? 0m,
                    Int(form["graceDays"]), Int(form["repeatDays"])), ct);
                return "done=saved";
            })).RequireAuthorization();

        app.MapPost("/sales/collections/settings/reminders", async (IFormCollection form, CollectionService collections, CancellationToken ct) =>
            await Back("/sales/collections/settings", "#reminders", async () =>
            {
                await collections.SaveRemindersAsync(new ReminderSettingsCommand(form["enabled"] == "true", form["schedule"], form["subject"], form["body"]), ct);
                return "done=saved";
            })).RequireAuthorization();

        // Fields: invoice (one per ticked row), amount:{invoiceId} (blank = the whole balance), date, accountId, memo.
        app.MapPost("/sales/write-offs", async (IFormCollection form, WriteOffService writeOffs, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            var back = form["back"].ToString() is { Length: > 0 } b && b.StartsWith("/sales/write-offs/new", StringComparison.Ordinal) ? b : "/sales/write-offs/new";
            try
            {
                var lines = (Ids(form["invoice"]) ?? []).Select(id => new WriteOffLine(id, Amount(form[$"amount:{id}"]))).ToList();
                var created = await writeOffs.WriteOffAsync(new WriteOffCommand(Date(form["date"]) ?? Today(), lines, Id(form["accountId"]), form["memo"]), ct);
                return Results.Redirect(created.Count == 1 ? $"/sales/write-offs/{created[0].Id}" : $"/sales/write-offs/new?done={created.Count}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                var join = back.Contains('?', StringComparison.Ordinal) ? "&" : "?";
                return Results.Redirect($"{back}{join}error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/sales/credit-memos/{id:guid}/applications/{invoiceId:guid}/remove", async (Guid id, Guid invoiceId, IFormCollection form, CreditService credits, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            try
            {
                await credits.UnapplyAsync(id, invoiceId, ct);
                return Results.Redirect($"/sales/credit-memos/{id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/sales/credit-memos/{id}?error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/sales/write-offs/{id:guid}/void", async (Guid id, IFormCollection form, WriteOffService writeOffs, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/sales/write-offs/{id}", "", async () =>
            {
                var reason = form["reason"].ToString().Trim();
                if (reason.Length == 0)
                    throw new DomainException("writeoff.reason_required", "Say why the write-off is being voided.");
                await writeOffs.VoidAsync(id, reason, ct);
                return "done=voided";
            });
        }).RequireAuthorization();

        return app;
    }

    /// <summary>Runs the change and goes back to the page with its outcome, or the refusal (asking for the closing password when that would help).</summary>
    private static async Task<IResult> Back(string page, string anchor, Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"{page}?{await change()}{anchor}");
        }
        catch (DomainException ex)
        {
            var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}{password}{anchor}");
        }
    }

    private static string ScheduledBack(IFormCollection form) =>
        form["back"].ToString() is { Length: > 0 } b && (b.StartsWith("/sales/invoices/", StringComparison.Ordinal) || b == "/sales/scheduled-emails")
            ? b : "/sales/scheduled-emails";

    private static string? Password(IFormCollection form) => form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    /// <summary>The ticked ids, or null when none were sent (meaning "nothing", not "all").</summary>
    private static HashSet<Guid>? Ids(Microsoft.Extensions.Primitives.StringValues values)
    {
        var ids = values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
        return ids.Count == 0 ? null : ids;
    }

    private static HashSet<Guid> Chosen(IFormCollection form) =>
        Ids(form["invoice"]) ?? throw new DomainException("collections.nothing_chosen", "Tick at least one invoice.");

    private static int Int(string? value) => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static decimal? Amount(string? value) =>
        StatementValues.TryParseAmount(value, out var amount) ? amount : null;

    private static DateOnly Today() => CompanyTime.Today;
}
