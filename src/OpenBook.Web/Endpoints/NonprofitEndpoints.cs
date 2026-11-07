using System.Globalization;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Segments;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Segments;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the nonprofit pages and a class's functional area (antiforgery-checked, form-bound).</summary>
public static class NonprofitEndpoints
{
    public static IEndpointRouteBuilder MapNonprofitEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: date, classId ("" = no class), amount, memo, ClosingPassword.
        app.MapPost("/nonprofit/releases", async (IFormCollection form, NonprofitService nonprofit, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            var date = Date(form["date"]) ?? CompanyTime.Today;
            try
            {
                var release = await nonprofit.ReleaseAsync(new ReleaseCommand(
                    date,
                    Guid.TryParse(form["classId"], out var c) ? c : null,
                    Number(form["amount"]) ?? throw new DomainException("release.amount", "Enter an amount above zero to release."),
                    form["memo"]), ct);
                return Results.Redirect($"/nonprofit/releases/{release.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/nonprofit?asOf={date:yyyy-MM-dd}&error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/nonprofit/releases/{id:guid}/void", async (Guid id, IFormCollection form, NonprofitService nonprofit, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            try
            {
                var reason = form["reason"].ToString().Trim() is { Length: > 0 } r ? r : throw new DomainException("void.reason_required", "Say why.");
                await nonprofit.VoidAsync(id, reason, ct);
                return Results.Redirect($"/nonprofit/releases/{id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/nonprofit/releases/{id}?error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/nonprofit/functional-classes", async (IFormCollection form, NonprofitService nonprofit, CancellationToken ct) =>
        {
            var changed = await nonprofit.SetUpFunctionalClassesAsync(ct);
            return Results.Redirect($"/nonprofit?classes={changed}");
        }).RequireAuthorization();

        // Gifts. Fields: donorId, date, amount, kind, debitAccountId, incomeAccountId, pledgeId, classId, reference, memo,
        // description, goodsValue, goodsDescription, ClosingPassword.
        app.MapPost("/nonprofit/gifts", async (IFormCollection form, GiftService gifts, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            try
            {
                var gift = await gifts.RecordAsync(new RecordGiftCommand(
                    Id(form["donorId"]) ?? throw new DomainException("donation.donor_required", "Choose the donor."),
                    Date(form["date"]) ?? CompanyTime.Today,
                    Number(form["amount"]) ?? throw new DomainException("donation.amount", "Enter an amount above zero."),
                    Id(form["debitAccountId"]) ?? Guid.Empty,
                    Enum.TryParse<GiftKind>(form["kind"], out var kind) ? kind : GiftKind.Cash,
                    Id(form["incomeAccountId"]),
                    Id(form["pledgeId"]),
                    Id(form["classId"]),
                    form["reference"],
                    form["memo"],
                    form["description"],
                    Number(form["goodsValue"]) ?? 0m,
                    form["goodsDescription"],
                    Enum.TryParse<OpenBook.Domain.Sales.PaymentMethod>(form["method"], out var method) ? method : null,
                    form["campaign"]), ct);
                return Results.Redirect($"/nonprofit/gifts/{gift.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/nonprofit/gifts/new?donor={form["donorId"]}&pledge={form["pledgeId"]}&kind={form["kind"]}&error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/nonprofit/gifts/{id:guid}/campaign", async (Guid id, IFormCollection form, GiftService gifts, CancellationToken ct) =>
        {
            try
            {
                await gifts.SetCampaignAsync(id, form["campaign"], ct);
                return Results.Redirect($"/nonprofit/gifts/{id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/nonprofit/gifts/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/nonprofit/gifts/{id:guid}/void", async (Guid id, IFormCollection form, GiftService gifts, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/gifts/{id}", () => gifts.VoidAsync(id, Reason(form), ct));
        }).RequireAuthorization();

        app.MapGet("/nonprofit/gifts/{id:guid}/letter", (Guid id, GiftService gifts, CancellationToken ct) =>
            Pdf($"/nonprofit/gifts/{id}", async () => (await gifts.AcknowledgmentAsync(id, ct)).Pdf)).RequireAuthorization();

        // Fields: id (one per ticked gift), back.
        app.MapPost("/nonprofit/thanks/mark", async (IFormCollection form, GiftService gifts, CancellationToken ct) =>
        {
            var back = form["back"].ToString() is { } b && b.StartsWith("/nonprofit/", StringComparison.Ordinal) ? b : "/nonprofit/thanks";
            var ids = form["id"].Select(Id).OfType<Guid>().ToList();
            var marked = await gifts.MarkAcknowledgedAsync(ids, ct);
            return Results.Redirect(back == "/nonprofit/thanks" ? $"{back}?done={Uri.EscapeDataString($"{marked} gift(s) marked as thanked.")}" : back);
        }).RequireAuthorization();

        // The ticked gifts' letters: one PDF to print, or each emailed (an emailed letter marks its gift thanked).
        app.MapPost("/nonprofit/thanks/letters", (IFormCollection form, GiftService gifts, CancellationToken ct) =>
            Pdf("/nonprofit/thanks", async () => (await gifts.AcknowledgmentsAsync([.. form["id"].Select(Id).OfType<Guid>()], ct)).Pdf)).RequireAuthorization();

        app.MapPost("/nonprofit/thanks/email", async (IFormCollection form, EmailService email, CancellationToken ct) =>
        {
            try
            {
                var ids = form["id"].Select(Id).OfType<Guid>().Distinct().ToList();
                if (ids.Count == 0)
                    throw new DomainException("donation.none_chosen", "Tick at least one gift.");
                var run = await email.SendEachAsync([.. ids.Select(id => new EmailSource(OpenBook.Domain.Email.EmailKind.GiftAcknowledgment, id))], ct);
                var notSent = run.NoEmail.Select(n => $"{n}: no email address").Concat(run.Failed.Select(f => $"{f.Name}: {f.Reason}")).ToList();
                var done = $"{run.Sent.Count} letter(s) emailed." + (notSent.Count > 0 ? $" Not sent: {string.Join("; ", notSent)}." : "");
                return Results.Redirect($"/nonprofit/thanks?done={Uri.EscapeDataString(done)}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/nonprofit/thanks?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapGet("/nonprofit/statements/{year:int}", (int year, GiftService gifts, CancellationToken ct) =>
            Pdf("/nonprofit/thanks", async () => (await gifts.YearStatementsAsync(year, ct)).Pdf)).RequireAuthorization();

        app.MapGet("/nonprofit/statements/{year:int}/{donorId:guid}", (int year, Guid donorId, GiftService gifts, CancellationToken ct) =>
            Pdf("/nonprofit/thanks", async () => (await gifts.YearStatementAsync(donorId, year, ct)).Pdf)).RequireAuthorization();

        app.MapPost("/nonprofit/statements/{year:int}/email", async (int year, IFormCollection form, GiftService gifts, EmailService email, CancellationToken ct) =>
        {
            try
            {
                var donors = await gifts.DonorsInYearAsync(year, ct);
                var run = await email.SendEachAsync([.. donors.Select(d => new EmailSource(OpenBook.Domain.Email.EmailKind.GivingStatement, d, Year: year))], ct);
                var notSent = run.NoEmail.Select(n => $"{n}: no email address").Concat(run.Failed.Select(f => $"{f.Name}: {f.Reason}")).ToList();
                var done = $"{run.Sent.Count} statement(s) sent." + (notSent.Count > 0 ? $" Not sent: {string.Join("; ", notSent)}." : "")
                    + (run.AlreadySent is { Count: > 0 } had ? $" {had.Count} already had theirs in the last hour." : "");
                return Results.Redirect($"/nonprofit/thanks?year={year}&done={Uri.EscapeDataString(done)}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/nonprofit/thanks?year={year}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        // Pledges. Fields: donorId, date, amount, dueDate, incomeAccountId, classId, memo, ClosingPassword.
        app.MapPost("/nonprofit/pledges", async (IFormCollection form, GiftService gifts, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            try
            {
                var pledge = await gifts.PledgeAsync(new PledgeCommand(
                    Id(form["donorId"]) ?? throw new DomainException("donation.donor_required", "Choose the donor."),
                    Date(form["date"]) ?? CompanyTime.Today,
                    Number(form["amount"]) ?? throw new DomainException("pledge.amount", "Enter an amount above zero."),
                    Id(form["incomeAccountId"]),
                    Id(form["classId"]),
                    Date(form["dueDate"]),
                    form["memo"]), ct);
                return Results.Redirect($"/nonprofit/pledges/{pledge.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/nonprofit/pledges/new?donor={form["donorId"]}&error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/nonprofit/pledges/{id:guid}/void", async (Guid id, IFormCollection form, GiftService gifts, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/pledges/{id}", () => gifts.VoidPledgeAsync(id, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/pledges/{id:guid}/cancel", async (Guid id, IFormCollection form, GiftService gifts, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/pledges/{id}", () => gifts.CancelRestAsync(id, Date(form["date"]) ?? CompanyTime.Today, ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/pledges/{id:guid}/uncancel", async (Guid id, IFormCollection form, GiftService gifts, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/pledges/{id}", () => gifts.UndoCancelAsync(id, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/settings", async (IFormCollection form, GiftService gifts, CancellationToken ct) =>
        {
            try
            {
                await gifts.SaveSettingsAsync(new GiftSettingsCommand(form["ein"], form["signerName"], form["signerTitle"], form["intro"]), ct);
                return Results.Redirect("/nonprofit/settings?saved=true");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/nonprofit/settings?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        // Grants. Fields: name, funderId, reference, amount, awardedOn, startsOn, endsOn, classId, incomeAccountId, conditional, terms.
        app.MapPost("/nonprofit/grants", async (IFormCollection form, GrantService grants, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            try
            {
                var today = CompanyTime.Today;
                var grant = await grants.CreateAsync(new GrantCommand(
                    form["name"].ToString(),
                    Id(form["funderId"]) ?? throw new DomainException("grant.funder_required", "Choose the funder."),
                    Number(form["amount"]) ?? throw new DomainException("grant.amount", "Enter the amount awarded."),
                    Date(form["awardedOn"]) ?? today,
                    Date(form["startsOn"]) ?? today,
                    Date(form["endsOn"]) ?? today,
                    Id(form["classId"]),
                    Id(form["incomeAccountId"]),
                    form["conditional"] == "true",
                    form["reference"],
                    form["terms"],
                    Id(form["parentClassId"])), ct);
                return Results.Redirect($"/nonprofit/grants/{grant.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/nonprofit/grants/new?error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/receive", async (Guid id, IFormCollection form, GrantService grants, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/grants/{id}", () => grants.ReceiveAsync(id, Date(form["date"]) ?? CompanyTime.Today,
                Number(form["amount"]) ?? 0m, Id(form["bankAccountId"]) ?? Guid.Empty, int.TryParse(form["requestLine"], out var request) ? request : null, ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/requests", async (Guid id, IFormCollection form, GrantService grants, CancellationToken ct) =>
            await Back($"/nonprofit/grants/{id}", () => grants.RequestAsync(id, Date(form["date"]) ?? CompanyTime.Today,
                Date(form["from"]) ?? CompanyTime.Today, Date(form["to"]) ?? CompanyTime.Today, FormNumbers.Amount(form["amount"]), ct))).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/requests/{line:int}/void", async (Guid id, int line, IFormCollection form, GrantService grants, CancellationToken ct) =>
            await Back($"/nonprofit/grants/{id}", () => grants.VoidRequestAsync(id, line, ct))).RequireAuthorization();

        app.MapGet("/nonprofit/grants/{id:guid}/requests/{line:int}/letter", async (Guid id, int line, GrantService grants, CancellationToken ct) =>
        {
            var letter = await grants.RequestLetterAsync(id, line, ct);
            return Results.File(letter.Pdf, "application/pdf", letter.FileName);
        }).RequireAuthorization();

        // Shared cost allocations.
        app.MapPost("/nonprofit/allocations/rules", async (IFormCollection form, AllocationService allocations, CancellationToken ct) =>
        {
            var shares = Enumerable.Range(0, 8).Select(i => new AllocationShareDraft(Id(form[$"class:{i}"]) ?? Guid.Empty, Number(form[$"percent:{i}"]) ?? 0m)).ToList();
            return await Back("/nonprofit/allocations", () => allocations.SaveAsync(Id(form["id"]),
                new AllocationRuleDetails(form["name"].ToString(), Id(form["accountId"]) ?? Guid.Empty, Id(form["poolClassId"]), shares), ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/allocations/rules/{id:guid}/active", async (Guid id, IFormCollection form, AllocationService allocations, CancellationToken ct) =>
            await Back("/nonprofit/allocations", () => allocations.SetActiveAsync(id, form["active"] == "true", ct))).RequireAuthorization();

        app.MapPost("/nonprofit/allocations/run", async (IFormCollection form, AllocationService allocations, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = form["closingPassword"];
            return await Back("/nonprofit/allocations", () => allocations.RunAsync(Date(form["from"]) ?? CompanyTime.Today, Date(form["to"]) ?? CompanyTime.Today, null, ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/allocations/runs/{id:guid}/void", async (Guid id, IFormCollection form, AllocationService allocations, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = form["closingPassword"];
            return await Back("/nonprofit/allocations", () => allocations.VoidAsync(id, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/recognize", async (Guid id, IFormCollection form, GrantService grants, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/grants/{id}", () => grants.RecognizeAsync(id, Date(form["through"]) ?? CompanyTime.Today, ct));
        }).RequireAuthorization();

        // Fields: account:{i}, amount:{i}.
        app.MapPost("/nonprofit/grants/{id:guid}/budget", async (Guid id, IFormCollection form, GrantService grants, CancellationToken ct) =>
        {
            var lines = form.Keys.Where(k => k.StartsWith("account:", StringComparison.Ordinal))
                .Select(k => (Account: Id(form[k]), Amount: Number(form["amount:" + k["account:".Length..]]) ?? 0m))
                .Where(l => l.Account is not null && l.Amount != 0m)
                .Select(l => (l.Account!.Value, l.Amount)).ToList();
            return await Back($"/nonprofit/grants/{id}", () => grants.SetBudgetAsync(id, lines, ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/receipts/{line:int}/void", async (Guid id, int line, IFormCollection form, GrantService grants, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/grants/{id}", () => grants.VoidReceiptAsync(id, line, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/reports", async (Guid id, IFormCollection form, GrantService grants, CancellationToken ct) =>
            await Back($"/nonprofit/grants/{id}", () => grants.AddReportDueAsync(id,
                Date(form["dueOn"]) ?? throw new DomainException("grant.report_date_required", "Enter the date it's due."), form["title"], ct))).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/reports/{line:int}/sent", async (Guid id, int line, IFormCollection form, GrantService grants, CancellationToken ct) =>
            await Back($"/nonprofit/grants/{id}", () => grants.MarkReportSentAsync(id, line, form["undo"] == "true" ? null : Date(form["sentOn"]) ?? CompanyTime.Today, ct))).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/reports/{line:int}/remove", async (Guid id, int line, IFormCollection _, GrantService grants, CancellationToken ct) =>
            await Back($"/nonprofit/grants/{id}", () => grants.RemoveReportDueAsync(id, line, ct))).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/reduce", async (Guid id, IFormCollection form, GrantService grants, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/grants/{id}", () => grants.ReduceAsync(id, Date(form["date"]) ?? CompanyTime.Today, Number(form["amount"]) ?? 0m, form["reason"], ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/return-advance", async (Guid id, IFormCollection form, GrantService grants, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/grants/{id}", () => grants.ReturnAdvanceAsync(id, Date(form["date"]) ?? CompanyTime.Today, Number(form["amount"]) ?? 0m,
                Id(form["bankAccountId"]) ?? Guid.Empty, form["reason"], ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/adjustments/{line:int}/void", async (Guid id, int line, IFormCollection form, GrantService grants, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/grants/{id}", () => grants.VoidAdjustmentAsync(id, line, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/recognitions/{line:int}/void", async (Guid id, int line, IFormCollection form, GrantService grants, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/grants/{id}", () => grants.VoidRecognitionAsync(id, line, Reason(form), ct));
        }).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/close", async (Guid id, IFormCollection form, GrantService grants, CancellationToken ct) =>
            await Back($"/nonprofit/grants/{id}", () => grants.CloseAsync(id, ct))).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/reopen", async (Guid id, IFormCollection form, GrantService grants, CancellationToken ct) =>
            await Back($"/nonprofit/grants/{id}", () => grants.ReopenAsync(id, ct))).RequireAuthorization();

        app.MapPost("/nonprofit/grants/{id:guid}/void", async (Guid id, IFormCollection form, GrantService grants, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/nonprofit/grants/{id}", () => grants.VoidAsync(id, Reason(form), ct));
        }).RequireAuthorization();

        // Recurring gifts. Fields: donorId, amount, depositAccountId, frequency, interval, startDate, endDate, maxOccurrences,
        // pledgeId, incomeAccountId, classId, memo, autoCreate.
        app.MapPost("/nonprofit/recurring", async (IFormCollection form, RecurringGiftService recurring, CancellationToken ct) =>
            await Back("/nonprofit/recurring", () => recurring.CreateAsync(RecurringDetails(form), ct))).RequireAuthorization();

        app.MapPost("/nonprofit/recurring/{id:guid}", async (Guid id, IFormCollection form, RecurringGiftService recurring, CancellationToken ct) =>
            await Back("/nonprofit/recurring", () => recurring.UpdateAsync(id, RecurringDetails(form), ct))).RequireAuthorization();

        app.MapPost("/nonprofit/recurring/{id:guid}/active", async (Guid id, IFormCollection form, RecurringGiftService recurring, CancellationToken ct) =>
            await Back("/nonprofit/recurring", () => recurring.SetActiveAsync(id, form["active"] == "true", ct))).RequireAuthorization();

        app.MapPost("/nonprofit/recurring/record", async (IFormCollection form, RecurringGiftService recurring, CancellationToken ct) =>
        {
            var results = await recurring.CreateDueAsync(CompanyTime.Today, automaticOnly: false, ct: ct);
            var created = results.Sum(r => r.Created.Count);
            var errors = results.Where(r => r.Error is not null).Select(r => r.Error!).ToList();
            var done = $"{created} gift(s) recorded." + (errors.Count > 0 ? $" Stopped: {string.Join("; ", errors)}" : "");
            return Results.Redirect($"/nonprofit/recurring?done={Uri.EscapeDataString(done)}");
        }).RequireAuthorization();

        // On a class's page; "" clears the area (the parent's applies).
        app.MapPost("/segments/{id:guid}/functional-area", async (Guid id, IFormCollection form, SegmentService segments, CancellationToken ct) =>
        {
            try
            {
                await segments.SetFunctionalAreaAsync(id, Enum.TryParse<FunctionalArea>(form["area"], out var area) ? area : null, ct);
                return Results.Redirect($"/segments/{id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/segments/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>A PDF, or back to the page with why not (a link kept in a tab or typed: a year with no gifts, a voided gift).</summary>
    private static async Task<IResult> Pdf(string page, Func<Task<byte[]>> make)
    {
        try
        {
            return Results.File(await make(), "application/pdf");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}");
        }
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

    private static RecurringGiftDetails RecurringDetails(IFormCollection form) => new(
        Id(form["donorId"]) ?? throw new DomainException("donation.donor_required", "Choose the donor."),
        Number(form["amount"]) ?? 0m,
        Id(form["depositAccountId"]) ?? throw new DomainException("donation.account_required", "Choose the bank account it goes into."),
        Enum.TryParse<OpenBook.Domain.Ledger.RecurrenceFrequency>(form["frequency"], out var frequency) ? frequency : OpenBook.Domain.Ledger.RecurrenceFrequency.Monthly,
        Date(form["startDate"]) ?? CompanyTime.Today,
        int.TryParse(form["interval"], out var interval) ? interval : 1,
        Date(form["endDate"]),
        int.TryParse(form["maxOccurrences"], out var max) ? max : null,
        form["autoCreate"] == "true",
        Id(form["incomeAccountId"]),
        Id(form["classId"]),
        Id(form["pledgeId"]),
        form["memo"]);

    private static string Reason(IFormCollection form) =>
        form["reason"].ToString().Trim() is { Length: > 0 } reason ? reason : throw new DomainException("void.reason_required", "Say why.");

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static string? Password(IFormCollection form) => form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static decimal? Number(string? value) => FormNumbers.Number(value);
}
