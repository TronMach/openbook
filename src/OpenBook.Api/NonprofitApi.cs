using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Nonprofit;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;

namespace OpenBook.Api;

public sealed record ReleaseResponse(Guid Id, DateOnly Date, Guid? ClassId, decimal Amount, string? Memo, DocumentStatus Status, Guid JournalEntryId)
{
    public static ReleaseResponse From(RestrictionRelease r) => new(r.Id, r.Date, r.ClassId, r.Amount, r.Memo, r.Status, r.JournalEntryId);
}

public sealed record GiftResponse(
    Guid Id, int Number, Guid DonorId, DateOnly Date, GiftKind Kind, decimal Amount, Guid DebitAccountId, Guid IncomeAccountId, Guid? PledgeId,
    Guid? ClassId, string? Reference, string? Memo, string? Description, decimal GoodsValue, string? GoodsDescription, decimal Deductible,
    DocumentStatus Status, Guid JournalEntryId, DateTimeOffset? AcknowledgedAt)
{
    public static GiftResponse From(Donation d) => new(d.Id, d.Number, d.DonorId, d.Date, d.Kind, d.Amount, d.DebitAccountId, d.IncomeAccountId, d.PledgeId,
        d.ClassId, d.Reference, d.Memo, d.Description, d.GoodsValue, d.GoodsDescription, d.Deductible, d.Status, d.JournalEntryId, d.AcknowledgedAt);
}

public sealed record PledgeResponse(
    Guid Id, int Number, Guid DonorId, string Donor, DateOnly Date, DateOnly? DueDate, decimal Amount, decimal Paid, decimal Balance,
    Guid IncomeAccountId, Guid? ClassId, string? Memo, DocumentStatus Status, Guid JournalEntryId)
{
    public static PledgeResponse From(PledgeBalance b) => new(b.Pledge.Id, b.Pledge.Number, b.Pledge.DonorId, b.Donor, b.Pledge.Date, b.Pledge.DueDate,
        b.Pledge.Amount, b.Paid, b.Balance, b.Pledge.IncomeAccountId, b.Pledge.ClassId, b.Pledge.Memo, b.Pledge.Status, b.Pledge.JournalEntryId);
}

public sealed record GiftSettingsResponse(string? Ein, string? SignerName, string? SignerTitle, string? Intro)
{
    public static GiftSettingsResponse From(GiftSettings s) => new(s.Ein, s.SignerName, s.SignerTitle, s.Intro);
}

public sealed record AcknowledgedRequest(IReadOnlyList<Guid> Ids);

public sealed record PledgeCancelRequest(DateOnly Date);

public sealed record GrantBudgetRequest(Guid AccountId, decimal Amount);

public sealed record GrantReceiptRequest(DateOnly Date, decimal Amount, Guid BankAccountId);

public sealed record GrantRequestRequest(DateOnly Date, DateOnly From, DateOnly To, decimal? Amount = null);

public sealed record GrantRecognizeRequest(DateOnly Through);

public sealed record GrantResponse(
    Guid Id, string Name, Guid FunderId, string Funder, string? Reference, decimal Amount, DateOnly AwardedOn, DateOnly StartsOn, DateOnly EndsOn,
    Guid ClassId, Guid IncomeAccountId, bool Conditional, GrantStatus Status, decimal Recognized, decimal Received, decimal Due, decimal Spent,
    decimal ReadyToRecognize, IReadOnlyList<GrantBudgetRow> Budget)
{
    public static GrantResponse From(GrantPosition p) => new(p.Grant.Id, p.Grant.Name, p.Grant.FunderId, p.Funder, p.Grant.Reference, p.Grant.Amount,
        p.Grant.AwardedOn, p.Grant.StartsOn, p.Grant.EndsOn, p.Grant.ClassId, p.Grant.IncomeAccountId, p.Grant.Conditional, p.Grant.Status,
        p.Grant.Recognized, p.Grant.Received, p.Grant.Due, p.Spent, p.ReadyToRecognize, p.Rows);
}

internal static class NonprofitApi
{
    public static void MapNonprofitApi(this RouteGroupBuilder api)
    {
        var nonprofit = api.MapGroup("/nonprofit");

        nonprofit.MapGet("/purposes", (DateOnly? asOf, NonprofitService s, CancellationToken ct) =>
            s.PurposesAsync(asOf ?? CompanyTime.Today, ct));
        nonprofit.MapGet("/releases", async (NonprofitService s, CancellationToken ct) => (await s.ListReleasesAsync(ct)).Select(ReleaseResponse.From));
        nonprofit.MapGet("/releases/{id:guid}", async (Guid id, NonprofitService s, CancellationToken ct) =>
            await s.GetReleaseAsync(id, ct) is { } r ? Results.Ok(ReleaseResponse.From(r)) : Results.NotFound());
        nonprofit.MapPost("/releases", async (ReleaseCommand r, NonprofitService s, CancellationToken ct) =>
        {
            var release = await s.ReleaseAsync(r, ct);
            return Results.Created($"/api/nonprofit/releases/{release.Id}", ReleaseResponse.From(release));
        });
        nonprofit.MapPost("/releases/{id:guid}/void", async (Guid id, VoidRequest r, NonprofitService s, CancellationToken ct) =>
            ReleaseResponse.From(await s.VoidAsync(id, r.Reason, ct)));
        nonprofit.MapGet("/gifts", async (DateOnly? from, DateOnly? to, Guid? donorId, GiftService s, CancellationToken ct) =>
            (await s.ListAsync(from, to, donorId, ct)).Select(GiftResponse.From));
        nonprofit.MapPost("/gifts/batch", async (GiftBatchCommand r, GiftService s, CancellationToken ct) =>
            (await s.RecordBatchAsync(r, ct)).Select(GiftResponse.From));
        nonprofit.MapPost("/gifts/import", async (HttpRequest request, Guid accountId, Guid? incomeAccountId, Guid? classId, bool? dryRun, GiftImportService s, CancellationToken ct) =>
        {
            var csv = await OpenBook.Application.Import.TextFiles.ReadAsync(request.Body, ct);
            return dryRun == true ? await s.PreviewAsync(csv, ct) : await s.ImportAsync(csv, new GiftBatchDefaults(accountId, incomeAccountId, classId), ct);
        }).Accepts<string>("text/csv");
        nonprofit.MapGet("/allocations/rules", (AllocationService s, CancellationToken ct) => s.RulesAsync(ct));
        nonprofit.MapPost("/allocations/rules", async (AllocationRuleDetails r, AllocationService s, CancellationToken ct) => await s.SaveAsync(null, r, ct));
        nonprofit.MapPut("/allocations/rules/{id:guid}", async (Guid id, AllocationRuleDetails r, AllocationService s, CancellationToken ct) => await s.SaveAsync(id, r, ct));
        nonprofit.MapGet("/allocations/preview", (DateOnly from, DateOnly to, AllocationService s, CancellationToken ct) => s.PreviewAsync(from, to, ct));
        nonprofit.MapPost("/allocations/run", (DateOnly from, DateOnly to, AllocationService s, CancellationToken ct) => s.RunAsync(from, to, null, ct));
        nonprofit.MapGet("/donors/{id:guid}/giving", (Guid id, GiftService s, CancellationToken ct) => s.GivingAsync(id, ct));
        nonprofit.MapGet("/gifts/{id:guid}", async (Guid id, GiftService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } g ? Results.Ok(GiftResponse.From(g)) : Results.NotFound());
        nonprofit.MapPost("/gifts", async (RecordGiftCommand r, GiftService s, CancellationToken ct) =>
        {
            var gift = await s.RecordAsync(r, ct);
            return Results.Created($"/api/nonprofit/gifts/{gift.Id}", GiftResponse.From(gift));
        });
        nonprofit.MapPost("/gifts/{id:guid}/void", async (Guid id, VoidRequest r, GiftService s, CancellationToken ct) =>
            GiftResponse.From(await s.VoidAsync(id, r.Reason, ct)));
        nonprofit.MapPost("/gifts/acknowledged", async (AcknowledgedRequest r, GiftService s, CancellationToken ct) =>
            new { Marked = await s.MarkAcknowledgedAsync(r.Ids, ct) });
        nonprofit.MapGet("/gifts/{id:guid}/letter", async (Guid id, GiftService s, CancellationToken ct) =>
        {
            var letter = await s.AcknowledgmentAsync(id, ct);
            return Results.File(letter.Pdf, "application/pdf", letter.FileName);
        });
        nonprofit.MapGet("/statements/{year:int}/{donorId:guid}", async (int year, Guid donorId, GiftService s, CancellationToken ct) =>
        {
            var statement = await s.YearStatementAsync(donorId, year, ct);
            return Results.File(statement.Pdf, "application/pdf", statement.FileName);
        });

        nonprofit.MapGet("/pledges", async (Guid? donorId, GiftService s, CancellationToken ct) =>
            (await s.ListPledgesAsync(donorId, ct)).Select(PledgeResponse.From));
        nonprofit.MapGet("/pledges/{id:guid}", async (Guid id, GiftService s, CancellationToken ct) =>
            await s.GetPledgeAsync(id, ct) is { } p ? Results.Ok(PledgeResponse.From(p)) : Results.NotFound());
        nonprofit.MapPost("/pledges", async (PledgeCommand r, GiftService s, CancellationToken ct) =>
        {
            var pledge = await s.PledgeAsync(r, ct);
            return Results.Created($"/api/nonprofit/pledges/{pledge.Id}", PledgeResponse.From((await s.GetPledgeAsync(pledge.Id, ct))!));
        });
        nonprofit.MapPost("/pledges/{id:guid}/void", async (Guid id, VoidRequest r, GiftService s, CancellationToken ct) =>
        {
            await s.VoidPledgeAsync(id, r.Reason, ct);
            return PledgeResponse.From((await s.GetPledgeAsync(id, ct))!);
        });

        nonprofit.MapPost("/pledges/{id:guid}/cancel", async (Guid id, PledgeCancelRequest r, GiftService s, CancellationToken ct) =>
        {
            await s.CancelRestAsync(id, r.Date, ct);
            return PledgeResponse.From((await s.GetPledgeAsync(id, ct))!);
        });
        nonprofit.MapPost("/pledges/{id:guid}/uncancel", async (Guid id, VoidRequest r, GiftService s, CancellationToken ct) =>
        {
            await s.UndoCancelAsync(id, r.Reason, ct);
            return PledgeResponse.From((await s.GetPledgeAsync(id, ct))!);
        });

        nonprofit.MapGet("/recurring-gifts", (RecurringGiftService s, CancellationToken ct) => s.ListAsync(ct));
        nonprofit.MapPost("/recurring-gifts", async (RecurringGiftDetails r, RecurringGiftService s, CancellationToken ct) =>
        {
            var template = await s.CreateAsync(r, ct);
            return Results.Created($"/api/nonprofit/recurring-gifts/{template.Id}", template);
        });
        nonprofit.MapPut("/recurring-gifts/{id:guid}", (Guid id, RecurringGiftDetails r, RecurringGiftService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        nonprofit.MapPut("/recurring-gifts/{id:guid}/active", (Guid id, SetActiveRequest r, bool? catchUp, RecurringGiftService s, CancellationToken ct) => s.SetActiveAsync(id, r.Active, catchUp == true, ct));
        nonprofit.MapPost("/recurring-gifts/record", async (RecurringGiftService s, CancellationToken ct) =>
            (await s.CreateDueAsync(CompanyTime.Today, automaticOnly: false, ct: ct))
                .Select(r => new { TemplateId = r.Template.Id, Created = r.Created.Select(g => g.Id), r.Error }));

        nonprofit.MapGet("/settings", async (GiftService s, CancellationToken ct) => GiftSettingsResponse.From(await s.GetSettingsAsync(ct)));
        nonprofit.MapPut("/settings", async (GiftSettingsCommand r, GiftService s, CancellationToken ct) => GiftSettingsResponse.From(await s.SaveSettingsAsync(r, ct)));

        nonprofit.MapGet("/grants", async (DateOnly? asOf, GrantService s, CancellationToken ct) =>
            (await s.PositionsAsync(asOf ?? CompanyTime.Today, ct)).Select(GrantResponse.From));
        nonprofit.MapGet("/grants/{id:guid}", async (Guid id, DateOnly? asOf, GrantService s, CancellationToken ct) =>
            await s.PositionAsync(id, asOf ?? CompanyTime.Today, ct) is { } p ? Results.Ok(GrantResponse.From(p)) : Results.NotFound());
        nonprofit.MapPost("/grants", async (GrantCommand r, GrantService s, CancellationToken ct) =>
        {
            var grant = await s.CreateAsync(r, ct);
            return Results.Created($"/api/nonprofit/grants/{grant.Id}", await Position(s, grant.Id, ct));
        });
        nonprofit.MapPut("/grants/{id:guid}", async (Guid id, GrantDetailsCommand r, GrantService s, CancellationToken ct) =>
        {
            await s.UpdateAsync(id, r, ct);
            return await Position(s, id, ct);
        });
        nonprofit.MapPut("/grants/{id:guid}/budget", async (Guid id, IReadOnlyList<GrantBudgetRequest> r, GrantService s, CancellationToken ct) =>
        {
            await s.SetBudgetAsync(id, r.Select(l => (l.AccountId, l.Amount)).ToList(), ct);
            return await Position(s, id, ct);
        });
        nonprofit.MapPost("/grants/{id:guid}/receipts", async (Guid id, GrantReceiptRequest r, GrantService s, CancellationToken ct) =>
        {
            await s.ReceiveAsync(id, r.Date, r.Amount, r.BankAccountId, ct: ct);
            return await Position(s, id, ct);
        });
        nonprofit.MapPost("/grants/{id:guid}/requests", async (Guid id, GrantRequestRequest r, GrantService s, CancellationToken ct) =>
        {
            await s.RequestAsync(id, r.Date, r.From, r.To, r.Amount, ct);
            return await Position(s, id, ct);
        });
        nonprofit.MapGet("/grants/{id:guid}/requests/{line:int}/letter", async (Guid id, int line, GrantService s, CancellationToken ct) =>
        {
            var letter = await s.RequestLetterAsync(id, line, ct);
            return Results.File(letter.Pdf, "application/pdf", letter.FileName);
        });
        nonprofit.MapPost("/grants/{id:guid}/recognize", async (Guid id, GrantRecognizeRequest r, GrantService s, CancellationToken ct) =>
        {
            await s.RecognizeAsync(id, r.Through, ct);
            return await Position(s, id, ct);
        });
        nonprofit.MapPost("/grants/{id:guid}/close", async (Guid id, GrantService s, CancellationToken ct) =>
        {
            await s.CloseAsync(id, ct);
            return await Position(s, id, ct);
        });
        nonprofit.MapPost("/grants/{id:guid}/void", async (Guid id, VoidRequest r, GrantService s, CancellationToken ct) =>
        {
            await s.VoidAsync(id, r.Reason, ct);
            return await Position(s, id, ct);
        });

        nonprofit.MapPost("/functional-classes", async (NonprofitService s, CancellationToken ct) =>
            new { Changed = await s.SetUpFunctionalClassesAsync(ct) });
    }

    private static async Task<GrantResponse> Position(GrantService s, Guid id, CancellationToken ct) =>
        GrantResponse.From((await s.PositionAsync(id, CompanyTime.Today, ct))!);
}
