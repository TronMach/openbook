using System.Globalization;
using OpenBook.Application;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Common;
using OpenBook.Domain.Settlements;
using OpenBook.Web.Components;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts for owner-operators, their trucks and charges.</summary>
public static class DispatchEndpoints
{
    public static IEndpointRouteBuilder MapDispatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dispatch/owners", async (IFormCollection form, OwnerService owners, CancellationToken ct) =>
        {
            try
            {
                var owner = await owners.CreateOwnerAsync(Owner(form), ct);
                return Results.Redirect($"/dispatch/owners/{owner.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/dispatch/owners?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/dispatch/owners/{id:guid}", async (Guid id, IFormCollection form, OwnerService owners, IRecordVersions versions, CancellationToken ct) =>
            await Back($"/dispatch/owners/{id}", async () =>
            {
                await SettingsVersions.EnsureUnchangedAsync<OwnerOperator>(versions, id, form, ct);
                await owners.UpdateOwnerAsync(id, Owner(form), form["active"] == "true", ct);
            })).RequireAuthorization();

        // One reserve: number (0 adds one), name, method, rate, target, opening, interest, active.
        app.MapPost("/dispatch/owners/{id:guid}/reserves", async (Guid id, IFormCollection form, OwnerService owners, CancellationToken ct) =>
            await Back($"/dispatch/owners/{id}#reserves", () =>
            {
                var number = int.TryParse(form["number"], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
                var method = Enum.TryParse<ReserveMethod>(form["method"], out var m) ? m : ReserveMethod.Fixed;
                return owners.SetReserveAsync(id, number, new OwnerReserveDraft(form["name"].ToString(), method,
                    Number(form, "rate", "rate") ?? 0m, Number(form, "target", "target"), Number(form, "opening", "opening balance") ?? 0m,
                    Number(form, "interest", "yearly interest"), form["active"] == "true" || number == 0), ct);
            })).RequireAuthorization();

        // One deduction: number (0 adds one), name, account, method, rate, upTo, priority, startsOn, endsOn, carry, active.
        app.MapPost("/dispatch/owners/{id:guid}/deductions", async (Guid id, IFormCollection form, OwnerService owners, CancellationToken ct) =>
            await Back($"/dispatch/owners/{id}#deductions", () =>
            {
                var number = int.TryParse(form["number"], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
                return owners.SetDeductionAsync(id, number, new OwnerDeductionDraft(form["name"].ToString(), Id(form, "accountId") ?? Guid.Empty,
                    Enum.TryParse<DeductionMethod>(form["method"], out var m) ? m : DeductionMethod.Fixed, Number(form, "rate", "rate") ?? 0m, Number(form, "upTo", "total"),
                    int.TryParse(form["priority"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 10,
                    DateOnly.TryParse(form["startsOn"], CultureInfo.InvariantCulture, out var from) ? from : null,
                    DateOnly.TryParse(form["endsOn"], CultureInfo.InvariantCulture, out var to) ? to : null,
                    form["carry"] == "true", form["active"] == "true" || number == 0), ct);
            })).RequireAuthorization();

        // A month's interest: month as yyyy-MM, its last day the date.
        app.MapPost("/dispatch/interest", async (IFormCollection form, OwnerService owners, CancellationToken ct) =>
            await Back("/dispatch/interest", () =>
            {
                if (!DateOnly.TryParseExact($"{form["month"]}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
                    throw new DomainException("interest.month_required", "Choose the month.");
                return owners.PostInterestAsync(first.AddMonths(1).AddDays(-1), Id(form, "accountId"), ct);
            })).RequireAuthorization();

        app.MapPost("/dispatch/interest/{id:guid}/void", async (Guid id, IFormCollection form, OwnerService owners, CancellationToken ct) =>
            await Back("/dispatch/interest", () => owners.VoidInterestAsync(id, form["reason"].ToString(), ct))).RequireAuthorization();

        // A stop's times, as the person's clock reads them in the company's time zone (datetime-local fields).
        app.MapPost("/dispatch/loads/{id:guid}/stops/{sequence:int}", async (Guid id, int sequence, IFormCollection form, LoadService loads, CancellationToken ct) =>
            await Back($"/dispatch/loads/{id}#stops", () => loads.RecordStopAsync(id, sequence, LocalTime(form["arrived"]), LocalTime(form["departed"]), ct))).RequireAuthorization();

        app.MapPost("/dispatch/loads/{id:guid}/invoice", async (Guid id, IFormCollection form, LoadService loads, CancellationToken ct) =>
        {
            try
            {
                var invoice = await loads.InvoiceAsync(id, DateOnly.TryParse(form["date"], CultureInfo.InvariantCulture, out var date) ? date : null, ct);
                return Results.Redirect($"/sales/invoices/{invoice.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/dispatch/loads/{id}?error={Uri.EscapeDataString(ex.Message)}#invoice");
            }
        }).RequireAuthorization();

        app.MapPost("/dispatch/loads/{id:guid}/detention", async (Guid id, IFormCollection form, LoadService loads, CancellationToken ct) =>
            await Back($"/dispatch/loads/{id}#invoice", () => loads.ChargeDetentionAsync(id, Number(form, "freeHours", "free time") ?? 2m,
                Number(form, "rate", "rate") ?? throw new DomainException("load.rate", "Enter the detention rate per hour."), Id(form, "itemId"), ct))).RequireAuthorization();

        // Pay rule: basis, rate, emptyMiles, pass:{charge} percents (blank for none).
        app.MapPost("/dispatch/owners/{id:guid}/pay", async (Guid id, IFormCollection form, OwnerService owners, CancellationToken ct) =>
            await Back($"/dispatch/owners/{id}#pay", () =>
            {
                var passes = new List<(LoadCharge, decimal)>();
                foreach (var charge in Enum.GetValues<LoadCharge>().Where(c => c != LoadCharge.Linehaul))
                {
                    if (Number(form, $"pass:{charge}", "share") is { } percent)
                        passes.Add((charge, percent));
                }

                return owners.SetPayAsync(id, Enum.TryParse<PayBasis>(form["basis"], out var basis) ? basis : PayBasis.Typed, Number(form, "rate", "pay rate") ?? 0m,
                    form["emptyMiles"] == "true", passes, ct);
            })).RequireAuthorization();

        app.MapPost("/dispatch/loads/{id:guid}/status", async (Guid id, IFormCollection form, LoadService loads, CancellationToken ct) =>
            await Back($"/dispatch/loads/{id}", () => form["status"].ToString() switch
            {
                "Cancelled" => loads.SetCancelledAsync(id, true, ct),
                "Restore" => loads.SetCancelledAsync(id, false, ct),
                var text => Enum.TryParse<LoadStatus>(text, out var status) ? loads.SetStatusAsync(id, status, ct)
                    : throw new DomainException("load.status_invalid", "Choose where the load is."),
            })).RequireAuthorization();

        app.MapPost("/dispatch/trucks", async (IFormCollection form, OwnerService owners, CancellationToken ct) =>
        {
            try
            {
                var truck = await owners.CreateTruckAsync(Truck(form), ct);
                return Results.Redirect(AccessRules.LocalPath(form["back"]) ?? $"/dispatch/trucks/{truck.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/dispatch/trucks?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/dispatch/trucks/{id:guid}", async (Guid id, IFormCollection form, OwnerService owners, IRecordVersions versions, CancellationToken ct) =>
            await Back($"/dispatch/trucks/{id}", async () =>
            {
                await SettingsVersions.EnsureUnchangedAsync<Truck>(versions, id, form, ct);
                await owners.UpdateTruckAsync(id, Truck(form), form["active"] == "true", ct);
            })).RequireAuthorization();

        app.MapPost("/dispatch/charges", async (IFormCollection form, OwnerService owners, CancellationToken ct) =>
        {
            var back = AccessRules.LocalPath(form["back"]) ?? "/dispatch/charges";
            try
            {
                var amount = Number(form, "amount", "amount") ?? throw new DomainException("charge.amount_required", "Enter the amount.");
                var charge = await owners.CreateChargeAsync(new OwnerChargeDetails(
                    Id(form, "ownerId") ?? Guid.Empty, DateOnly.TryParse(form["date"], CultureInfo.InvariantCulture, out var date) ? date : CompanyTime.Today,
                    Enum.TryParse<OwnerChargeKind>(form["kind"], out var kind) ? kind : OwnerChargeKind.Other, amount,
                    form["paidFrom"] == "books" ? null : Id(form, "paidFrom") ?? throw new DomainException("charge.paid_from_required", "Choose where the money came from."),
                    form["description"].ToString(), form["reference"].ToString(), Id(form, "truckId"), Number(form, "gallons", "gallons"), form["counted1099"] == "true"), ct);
                return Results.Redirect($"/dispatch/charges/{charge.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}{(back.Contains('?', StringComparison.Ordinal) ? '&' : '?')}error={Uri.EscapeDataString(ex.Message)}#new");
            }
        }).RequireAuthorization();

        app.MapGet("/dispatch/settlements/{id:guid}/pdf", async (Guid id, OwnerStatements statements, CancellationToken ct) =>
        {
            var form = await statements.RenderAsync(id, ct);
            return Results.File(form.Pdf, "application/pdf", form.FileName);
        }).RequireAuthorization();

        app.MapPost("/dispatch/settlements/{id:guid}/void", async (Guid id, IFormCollection form, OwnerSettlementService settlements, CancellationToken ct) =>
            await Back($"/dispatch/settlements/{id}", () => settlements.VoidAsync(id, form["reason"].ToString(), ct))).RequireAuthorization();

        // Form-bound (the file), so the page's antiforgery token is checked automatically.
        app.MapPost("/dispatch/charges/import", async (IFormFile? file, [Microsoft.AspNetCore.Mvc.FromForm] string? kind, [Microsoft.AspNetCore.Mvc.FromForm] string? paidFrom,
            OwnerService owners, CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.Redirect($"/dispatch/charges/import?error={Uri.EscapeDataString("Choose the file to import.")}");
            if (file.Length > SettlementEndpoints.MaxFileBytes)
                return Results.Redirect($"/dispatch/charges/import?error={Uri.EscapeDataString("The file is larger than 5 MB.")}");
            using var memory = new MemoryStream();
            await file.CopyToAsync(memory, ct);
            try
            {
                Guid? from = paidFrom == "books" ? null : Guid.TryParse(paidFrom, out var account) ? account
                    : throw new DomainException("charge.paid_from_required", "Choose where the money came from.");
                var result = await owners.ImportChargesAsync(Enum.TryParse<OwnerChargeKind>(kind, out var k) ? k : OwnerChargeKind.Fuel, from, memory.ToArray(), save: true, ct);
                var left = result.Rows.Count - result.Charged;
                var note = string.Join(" ", result.Warnings);
                return Results.Redirect($"/dispatch/charges/import?imported={result.Charged}&left={left}&total={result.Total.ToString(CultureInfo.InvariantCulture)}{(note.Length > 0 ? $"&note={Uri.EscapeDataString(note)}" : "")}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/dispatch/charges/import?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(SettlementEndpoints.MaxFileBytes * 2));

        app.MapPost("/dispatch/charges/{id:guid}/void", async (Guid id, IFormCollection form, OwnerService owners, CancellationToken ct) =>
            await Back($"/dispatch/charges/{id}", () => owners.VoidChargeAsync(id, form["reason"].ToString(), ct))).RequireAuthorization();

        return app;
    }

    internal static Guid? Id(IFormCollection form, string name) => Guid.TryParse(form[name], out var id) && id != Guid.Empty ? id : null;

    /// <summary>A typed amount or rate (through <see cref="FormNumbers"/>); blank is null.</summary>
    internal static decimal? Number(IFormCollection form, string name, string what) => FormNumbers.Number(form[name]);

    /// <summary>A datetime-local field's value in the company's time zone, in UTC; blank is null.</summary>
    internal static DateTimeOffset? LocalTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new DomainException("form.time_unreadable", $"'{text.Trim()}' isn't a date and time OpenBook can read.");
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), CompanyTime.Zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static OwnerDetails Owner(IFormCollection form) => new(
        Id(form, "vendorId") ?? Guid.Empty, Id(form, "payAccountId"), Id(form, "receivableAccountId"), Id(form, "reserveAccountId"), Id(form, "classId"), form["memo"].ToString());

    private static TruckDetails Truck(IFormCollection form) => new(
        form["unit"].ToString(), Id(form, "ownerId"), Id(form, "classId"), form["vin"].ToString(), form["plate"].ToString(),
        int.TryParse(form["year"], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null, form["make"].ToString());

    internal static async Task<IResult> Back(string page, Func<Task> change)
    {
        try
        {
            await change();
            return Results.Redirect(page);
        }
        catch (DomainException ex)
        {
            var hash = page.IndexOf('#', StringComparison.Ordinal);
            var (path, fragment) = hash < 0 ? (page, "") : (page[..hash], page[hash..]);
            return Results.Redirect($"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}error={Uri.EscapeDataString(ex.Message)}{fragment}");
        }
    }
}
