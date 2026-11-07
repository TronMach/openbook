using System.Globalization;
using OpenBook.Application.Assets;
using OpenBook.Application.Ledger;
using OpenBook.Application;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Common;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the fixed asset, depreciation and loan pages (antiforgery-checked, form-bound).</summary>
public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/assets", async (IFormCollection form, FixedAssetService assets, CancellationToken ct) =>
        {
            try
            {
                var asset = await assets.CreateAsync(Details(form), ct);
                return Results.Redirect($"/assets/{asset.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/assets/new?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/assets/{id:guid}", async (Guid id, IFormCollection form, FixedAssetService assets, CancellationToken ct) =>
        {
            try
            {
                await assets.UpdateAsync(id, Details(form), ct);
                return Results.Redirect($"/assets/{id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/assets/{id}/edit?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/assets/{id:guid}/dispose", async (Guid id, IFormCollection form, FixedAssetService assets, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/assets/{id}", () => assets.DisposeAsync(id, new DisposeAssetCommand(
                Date(form["date"]) ?? Today(), Number(form["proceeds"]) ?? 0m, Id(form["proceedsAccountId"])), ct));
        }).RequireAuthorization();

        app.MapPost("/assets/{id:guid}/remove", async (Guid id, IFormCollection _, FixedAssetService assets, CancellationToken ct) =>
            await Back($"/assets/{id}", () => assets.RemoveAsync(id, ct))).RequireAuthorization();

        app.MapPost("/assets/{id:guid}/put-back", async (Guid id, IFormCollection _, FixedAssetService assets, CancellationToken ct) =>
            await Back($"/assets/{id}", () => assets.PutBackAsync(id, ct))).RequireAuthorization();

        app.MapPost("/assets/{id:guid}/undispose", async (Guid id, IFormCollection form, FixedAssetService assets, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/assets/{id}", () => assets.UndoDisposalAsync(id, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/assets/depreciation", async (IFormCollection form, DepreciationService depreciation, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            var through = Date(form["through"]) ?? Today();
            try
            {
                var run = await depreciation.RunAsync(through, ct);
                return Results.Redirect($"/assets/runs/{run.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/assets?through={through:yyyy-MM-dd}&error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/assets/runs/{id:guid}/void", async (Guid id, IFormCollection form, DepreciationService depreciation, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/assets/runs/{id}", () => depreciation.VoidAsync(id, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/loans", async (IFormCollection form, LoanService loans, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            try
            {
                var loan = await loans.CreateAsync(new CreateLoanCommand(
                    new LoanDetails(
                        form["name"].ToString(),
                        Id(form["liabilityAccountId"]) ?? throw new DomainException("loan.account_required", "Choose the loan account."),
                        Id(form["interestAccountId"]) ?? Guid.Empty,
                        Number(form["principal"]) ?? throw new DomainException("loan.principal_not_positive", "Enter the amount borrowed."),
                        Number(form["rate"]) ?? throw new DomainException("loan.rate_invalid", "Enter the interest rate."),
                        int.TryParse(form["payments"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
                        Date(form["firstPaymentDate"]) ?? throw new DomainException("loan.first_payment_required", "Enter the first payment's date."),
                        Number(form["paymentAmount"]),
                        CreditLimit: Number(form["creditLimit"]),
                        FactorFee: Number(form["factorFee"])),
                    Id(form["proceedsAccountId"]),
                    Date(form["proceedsDate"])), ct);
                return Results.Redirect($"/loans/{loan.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/loans?error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/loans/{id:guid}/details", async (Guid id, IFormCollection form, LoanService loans, CancellationToken ct) =>
            await Back($"/loans/{id}", () => loans.DescribeAsync(id, form["name"].ToString(), form["notes"].ToString(),
                form.ContainsKey("classId") ? Id(form["classId"]) ?? Guid.Empty : null, ct))).RequireAuthorization();

        app.MapPost("/loans/{id:guid}/void", async (Guid id, IFormCollection form, LoanService loans, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/loans/{id}", () => loans.VoidAsync(id, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/loans/{id:guid}/pay", async (Guid id, IFormCollection form, LoanService loans, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/loans/{id}", () => loans.PayAsync(id, new LoanPaymentCommand(
                Date(form["date"]) ?? Today(),
                Id(form["paymentAccountId"]) ?? throw new DomainException("loan.payment_account", "Choose the account it was paid from."),
                Number(form["amount"])), ct));
        }).RequireAuthorization();

        app.MapPost("/loans/{id:guid}/draw", async (Guid id, IFormCollection form, LoanService loans, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/loans/{id}", () => loans.DrawAsync(id, Date(form["date"]) ?? Today(), Number(form["amount"]) ?? 0m,
                Id(form["bankAccountId"]) ?? throw new DomainException("loan.draw_account", "Choose the account the money went into."), ct));
        }).RequireAuthorization();

        app.MapPost("/loans/{id:guid}/draws/{drawId:guid}/void", async (Guid id, Guid drawId, IFormCollection form, LoanService loans, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/loans/{id}", () => loans.VoidDrawAsync(id, drawId, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/loans/{id:guid}/payments/{paymentId:guid}/void", async (Guid id, Guid paymentId, IFormCollection form, LoanService loans, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/loans/{id}", () => loans.VoidPaymentAsync(id, paymentId, Reason(form), ct));
        }).RequireAuthorization();

        return app;
    }

    private static FixedAssetDetails Details(IFormCollection form)
    {
        var method = Enum.TryParse<DepreciationMethod>(form["method"], out var m) ? m : DepreciationMethod.StraightLine;
        var years = Number(form["lifeYears"]) ?? 0m;
        return new FixedAssetDetails(
            form["name"].ToString(),
            Id(form["assetAccountId"]) ?? throw new DomainException("asset.account_required", "Choose the asset account."),
            Id(form["accumulatedAccountId"]) ?? Guid.Empty,
            Id(form["expenseAccountId"]) ?? Guid.Empty,
            Date(form["inServiceDate"]) ?? throw new DomainException("asset.date_required", "Enter the date it was placed in service."),
            Number(form["cost"]) ?? 0m,
            method,
            (int)decimal.Round(years * 12m, MidpointRounding.AwayFromZero),
            Number(form["salvage"]) ?? 0m,
            Number(form["decliningPercent"]) ?? 200m,
            Enum.TryParse<MacrsClass>(form["macrsClass"], out var c) ? c : null,
            Number(form["priorDepreciation"]) ?? 0m,
            Date(form["priorThrough"]),
            form["number"],
            form["description"],
            Id(form["classId"]),
            form["firstYearAll"] == "true"
                ? (method == DepreciationMethod.Macrs ? Number(form["cost"]) ?? 0m : (Number(form["cost"]) ?? 0m) - (Number(form["salvage"]) ?? 0m))
                : Number(form["firstYearAmount"]) ?? 0m);
    }

    private static async Task<IResult> Back<T>(string page, Func<Task<T>> change)
    {
        try
        {
            await change();
            return Results.Redirect(page);
        }
        catch (DomainException ex)
        {
            var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}{password}");
        }
    }

    private static string Reason(IFormCollection form) =>
        form["reason"].ToString().Trim() is { Length: > 0 } reason ? reason : throw new DomainException("void.reason_required", "Say why.");

    private static string? Password(IFormCollection form) => form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static decimal? Number(string? value) => FormNumbers.Number(value);

    private static DateOnly Today() => CompanyTime.Today;
}
