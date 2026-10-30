using System.Globalization;
using System.Text;
using Microsoft.Net.Http.Headers;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the purchase order, approval and ACH pages (antiforgery-checked, form-bound).</summary>
public static class PurchasingEndpoints
{
    public static IEndpointRouteBuilder MapPurchasingEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: qty:{lineNumber}, date, note.
        app.MapPost("/purchases/orders/{id:guid}/receive", async (Guid id, IFormCollection form, PurchaseOrderService orders, CancellationToken ct) =>
            await Back($"/purchases/orders/{id}", async () =>
            {
                var lines = Numbered(form, "qty:").Select(x => new ReceiveLine(x.Line, x.Value)).ToList();
                await orders.ReceiveAsync(id, new ReceiveGoodsCommand(Date(form["date"]) ?? Today(), lines, form["note"]), ct);
                return "done=received";
            })).RequireAuthorization();

        app.MapPost("/purchases/orders/{id:guid}/receipts/{number:int}/void", async (Guid id, int number, PurchaseOrderService orders, CancellationToken ct) =>
            await Back($"/purchases/orders/{id}", async () =>
            {
                await orders.VoidReceiptAsync(id, number, ct);
                return "done=received";
            })).RequireAuthorization();

        // Fields: qty:{lineNumber} (item lines), cost:{lineNumber} (unit cost, or a category line's amount), reference, date.
        app.MapPost("/purchases/orders/{id:guid}/bill", async (Guid id, IFormCollection form, PurchaseOrderService orders, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;
            try
            {
                var quantities = Numbered(form, "qty:").ToDictionary(x => x.Line, x => x.Value);
                var lines = Numbered(form, "cost:")
                    .Select(x => new BillOrderLine(x.Line, quantities.TryGetValue(x.Line, out var q) ? q : 1m, x.Value))
                    .Where(l => l.Quantity != 0m && (quantities.ContainsKey(l.LineNumber) || l.UnitCost != 0m))
                    .ToList();
                var bill = await orders.CreateBillAsync(id, new BillFromOrderCommand(Date(form["date"]) ?? Today(), form["reference"], lines), ct);
                return Results.Redirect($"/purchases/bills/{bill.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=1" : "";
                return Results.Redirect($"/purchases/orders/{id}?error={Uri.EscapeDataString(ex.Message)}{password}#bill");
            }
        }).RequireAuthorization();

        app.MapGet("/purchases/orders/{id:guid}/pdf", async (Guid id, PurchaseOrderForms forms, CancellationToken ct) =>
            Results.File((await forms.RenderAsync(id, ct)).Pdf, "application/pdf")).RequireAuthorization();

        app.MapPost("/purchases/orders/{id:guid}/email", async (Guid id, IFormCollection form, PurchaseOrderForms forms, CancellationToken ct) =>
        {
            try
            {
                await forms.SendAsync(id, new OpenBook.Application.Forms.EmailDraft(form["to"].ToString(), form["subject"].ToString(), form["body"].ToString()), ct);
                return Results.Redirect($"/purchases/orders/{id}?done=emailed");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/purchases/orders/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/purchases/orders/{id:guid}/cancel", async (Guid id, PurchaseOrderService orders, CancellationToken ct) =>
            await Back($"/purchases/orders/{id}", async () =>
            {
                await orders.CancelAsync(id, ct);
                return "done=cancelled";
            })).RequireAuthorization();

        // Fields: decision (approve/reject), note.
        app.MapPost("/purchases/approvals/{billId:guid}", async (Guid billId, IFormCollection form, BillApprovalService approvals, CancellationToken ct) =>
            await Back("/purchases/approvals", async () =>
            {
                if (form["decision"] == "reject")
                {
                    await approvals.RejectAsync(billId, form["note"], ct);
                    return "done=rejected";
                }

                await approvals.ApproveAsync(billId, form["note"], ct);
                return "done=approved";
            })).RequireAuthorization();

        app.MapPost("/purchases/approvals/settings", async (IFormCollection form, BillApprovalService approvals, CancellationToken ct) =>
            await Back("/purchases/approvals/settings", async () =>
            {
                await approvals.SaveSettingsAsync(new PurchasingSettingsCommand(
                    Amount(form["threshold"]), Amount(form["tolerance"]) ?? 0m, form["requireReceipt"] == "true", form["allowSelfApproval"] == "true", form["receiveIntoStock"] == "true"), ct);
                return "done=saved";
            })).RequireAuthorization();

        app.MapPost("/purchases/ach/settings", async (IFormCollection form, AchService ach, CancellationToken ct) =>
        {
            var accountId = Id(form["accountId"]) ?? Guid.Empty;
            return await Back($"/purchases/ach?accountId={accountId}&", async () =>
            {
                await ach.SaveSettingsAsync(accountId, new AchSettingsCommand(form["destinationRouting"].ToString(), form["destinationName"].ToString(),
                    form["originId"].ToString(), form["originName"].ToString(), form["companyId"].ToString(), form["companyName"].ToString(), form["entryDescription"]), ct);
                return "done=saved";
            });
        }).RequireAuthorization();

        // Fields: accountId, payment (one per ticked row), effectiveDate. Makes the file and goes to its download.
        app.MapPost("/purchases/ach/file", async (IFormCollection form, AchService ach, CancellationToken ct) =>
        {
            var accountId = Id(form["accountId"]) ?? Guid.Empty;
            try
            {
                var ids = form["payment"].Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
                var batch = await ach.CreateFileAsync(accountId, Date(form["effectiveDate"]) ?? Today().AddDays(1), ids, ct);
                return Results.Redirect($"/purchases/ach/files/{batch.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/purchases/ach?accountId={accountId}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapGet("/purchases/ach/files/{id:guid}", async (Guid id, HttpContext http, AchService ach, CancellationToken ct) =>
        {
            if (await ach.GetBatchAsync(id, ct) is not { } batch)
                return Results.NotFound();
            var name = FormattableString.Invariant($"ach-{batch.CreatedAt.ToLocalTime():yyyyMMdd}-{batch.FileIdModifier}.txt");
            http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = name }.ToString();
            return Results.Bytes(Encoding.ASCII.GetBytes(batch.Content), "text/plain");
        }).RequireAuthorization();

        // Fields: routing, accountNumber, kind, action (save/remove).
        app.MapPost("/purchases/ach/vendors/{vendorId:guid}", async (Guid vendorId, IFormCollection form, AchService ach, CancellationToken ct) =>
        {
            try
            {
                if (form["action"] == "remove")
                    await ach.RemoveVendorBankAccountAsync(vendorId, ct);
                else
                    await ach.SetVendorBankAccountAsync(vendorId, new VendorBankAccountCommand(form["routing"].ToString(), form["accountNumber"].ToString(),
                        Enum.TryParse<BankAccountKind>(form["kind"], out var kind) ? kind : BankAccountKind.Checking), ct);
                return Results.Redirect($"/vendors/{vendorId}#bank");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/vendors/{vendorId}?bankError={Uri.EscapeDataString(ex.Message)}#bank");
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>Fields named prefix + line number with a number in them.</summary>
    private static IEnumerable<(int Line, decimal Value)> Numbered(IFormCollection form, string prefix)
    {
        foreach (var (field, value) in form)
        {
            if (!field.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(field[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var line))
                continue;
            var text = value.ToString().Trim();
            if (text.Length == 0)
                continue;
            yield return (line, StatementValues.TryParseAmount(text, out var amount) || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
                ? amount
                : throw new DomainException("purchaseorder.number_invalid", $"'{text}' isn't a number."));
        }
    }

    /// <param name="page">The page, ending in "?" or "&amp;" when it has a query already.</param>
    private static async Task<IResult> Back(string page, Func<Task<string>> change)
    {
        var join = page.EndsWith('&') ? "" : "?";
        try
        {
            return Results.Redirect($"{page}{join}{await change()}");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}{join}error={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static decimal? Amount(string? value) =>
        StatementValues.TryParseAmount(value, out var amount) ? amount : null;

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);
}
