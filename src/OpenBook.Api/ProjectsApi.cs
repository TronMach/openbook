using OpenBook.Application.Companies;
using OpenBook.Domain.Companies;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Projects;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;

namespace OpenBook.Api;

public sealed record ProjectResponse(
    Guid Id, Guid CustomerId, string Name, ProjectStatus Status, DateOnly? StartDate, DateOnly? EndDate, decimal? BudgetRevenue, decimal? BudgetCost,
    decimal? BudgetHours, string? Description)
{
    public static ProjectResponse From(ProjectSummary s) => new(
        s.Project.Id, s.Customer.Id, s.FullName, s.Project.Status, s.Project.StartDate, s.Project.EndDate, s.Project.BudgetRevenue, s.Project.BudgetCost,
        s.Project.BudgetHours, s.Project.Description);
}

public sealed record ProjectFiguresResponse(
    Guid ProjectId, decimal Income, decimal ExpenseCost, decimal LaborCost, decimal MileageCost, decimal Cost, decimal Profit, decimal? Margin, decimal Hours,
    decimal UnbilledTime, decimal UnbilledExpenses, decimal UnbilledMileage)
{
    public static ProjectFiguresResponse From(ProjectFigures f) => new(
        f.Project.Project.Id, f.Income, f.ExpenseCost, f.LaborCost, f.MileageCost, f.Cost, f.Profit, f.Margin, f.Hours, f.UnbilledTime, f.UnbilledExpenses, f.UnbilledMileage);
}

/// <param name="CostRate">What an hour costs; null below the accountant level.</param>
public sealed record WorkerResponse(Guid Id, string Name, decimal? CostRate, decimal? BillRate, string? Username, Guid? VendorId, bool IsActive, string? Role = null, decimal? WeeklyHours = null)
{
    public static WorkerResponse From(Worker w, ClaimsPrincipal user) =>
        new(w.Id, w.Name, ProjectsApi.SeesCosts(user) ? w.CostRate : null, w.BillRate, w.Username, w.VendorId, w.IsActive) { Role = w.Role, WeeklyHours = w.WeeklyHours };
}

public sealed record TimeEntryResponse(
    Guid Id, Guid WorkerId, DateOnly Date, decimal Hours, Guid? CustomerId, Guid? ItemId, bool Billable, decimal BillRate, decimal? CostRate,
    decimal Amount, decimal? Cost, string? Notes, DateTimeOffset? StartedAt, Guid? InvoiceId)
{
    public static TimeEntryResponse From(TimeEntry t, ClaimsPrincipal user)
    {
        var costs = ProjectsApi.SeesCosts(user);
        return new(t.Id, t.WorkerId, t.Date, t.Hours, t.CustomerId, t.ItemId, t.Billable, t.BillRate, costs ? t.CostRate : null, t.Amount, costs ? t.Cost : null,
            t.Notes, t.StartedAt, t.InvoiceId);
    }
}

public sealed record MileageTripResponse(
    Guid Id, DateOnly Date, decimal Miles, string Purpose, Guid? WorkerId, string? Vehicle, string? FromPlace, string? ToPlace, Guid? CustomerId,
    bool Billable, decimal BillRate, decimal Amount, Guid? InvoiceId)
{
    public static MileageTripResponse From(MileageTrip t) => new(
        t.Id, t.Date, t.Miles, t.Purpose, t.WorkerId, t.Vehicle, t.FromPlace, t.ToPlace, t.CustomerId, t.Billable, t.BillRate, t.Amount, t.InvoiceId);
}

public sealed record MileageRateRequest(int Year, decimal Rate);

public sealed record RaiseRatesRequest(decimal Percent, DateOnly From, bool WholeUnits = false);

public sealed record RepriceTimeRequest(DateOnly From, DateOnly? To = null, Guid? CustomerId = null);

public static class ProjectsApi
{
    /// <summary>Whether this caller sees what people cost: those who may change the rates.</summary>
    internal static bool SeesCosts(ClaimsPrincipal user) => AuthClaims.LevelIn(user, "projects") >= UserRole.Accountant;

    public static void MapProjectsApi(this RouteGroupBuilder api)
    {
        api.MapPut("/customers/{id:guid}/responsible", async (Guid id, ResponsibleRequest r, ProjectService s, CancellationToken ct) =>
        {
            var c = await s.SetResponsibleAsync(id, r.WorkerId, ct);
            return new { c.Id, c.ResponsibleWorkerId };
        });

        var projects = api.MapGroup("/projects");

        projects.MapGet("", async (bool? includeClosed, ProjectService s, CancellationToken ct) =>
            (await s.ListAsync(includeClosed ?? true, ct)).Select(ProjectResponse.From));
        projects.MapGet("/{id:guid}", async (Guid id, ProjectService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } p ? Results.Ok(ProjectResponse.From(p)) : Results.NotFound());
        projects.MapGet("/{id:guid}/figures", async (Guid id, DateOnly? from, DateOnly? to, ProjectService s, ProjectReports reports, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } p
                ? Results.Ok(ProjectFiguresResponse.From(await reports.FiguresAsync(p, from, to ?? DateOnly.MaxValue, ct)))
                : Results.NotFound());
        projects.MapPost("", async (CreateProjectCommand r, ProjectService s, CancellationToken ct) =>
        {
            var project = await s.CreateAsync(r, ct);
            return Results.Created($"/api/projects/{project.Id}", ProjectResponse.From((await s.GetAsync(project.Id, ct))!));
        });
        projects.MapPut("/{id:guid}", async (Guid id, ProjectDetails r, ProjectService s, CancellationToken ct) =>
        {
            await s.UpdateAsync(id, r, ct);
            return ProjectResponse.From((await s.GetAsync(id, ct))!);
        });

        projects.MapGet("/workers", async (bool? includeInactive, WorkerService s, ClaimsPrincipal user, CancellationToken ct) =>
            (await s.ListAsync(includeInactive ?? false, ct)).Select(w => WorkerResponse.From(w, user)));
        projects.MapPost("/workers", async (WorkerDetails r, WorkerService s, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var worker = await s.CreateAsync(r, ct);
            return Results.Created($"/api/projects/workers/{worker.Id}", WorkerResponse.From(worker, user));
        });
        projects.MapPut("/workers/{id:guid}", async (Guid id, WorkerDetails r, WorkerService s, ClaimsPrincipal user, CancellationToken ct) =>
            WorkerResponse.From(await s.UpdateAsync(id, r, ct), user));
        projects.MapPost("/workers/{id:guid}/active", async (Guid id, bool active, WorkerService s, ClaimsPrincipal user, CancellationToken ct) =>
            WorkerResponse.From(await s.SetActiveAsync(id, active, ct), user));

        // The week grid: rows of client, service, billable and hours Monday to Sunday (null leaves a day as it is).
        projects.MapGet("/time/week", async (Guid workerId, DateOnly monday, TimeService s, CancellationToken ct) => await s.WeekAsync(workerId, monday, ct));
        projects.MapPut("/time/week", async (Guid workerId, DateOnly monday, IReadOnlyList<WeekRow> rows, TimeService s, CancellationToken ct) =>
            new { Saved = await s.SaveWeekAsync(workerId, monday, rows ?? [], ct) });
        projects.MapGet("/time", async (DateOnly? from, DateOnly? to, Guid? workerId, Guid? customerId, TimeService s, ClaimsPrincipal user, CancellationToken ct) =>
            (await s.ListAsync(new TimeFilter(from, to, workerId, customerId), ct)).Select(t => TimeEntryResponse.From(t, user)));
        projects.MapPost("/time", async (TimeEntryDetails r, TimeService s, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var entry = await s.CreateAsync(r, ct);
            return Results.Created($"/api/projects/time/{entry.Id}", TimeEntryResponse.From(entry, user));
        });
        projects.MapPut("/time/{id:guid}", async (Guid id, TimeEntryDetails r, TimeService s, ClaimsPrincipal user, CancellationToken ct) =>
            TimeEntryResponse.From(await s.UpdateAsync(id, r, ct), user));
        projects.MapDelete("/time/{id:guid}", async (Guid id, TimeService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        projects.MapPost("/time/start", async (TimeEntryDetails r, TimeService s, ClaimsPrincipal user, CancellationToken ct) =>
            TimeEntryResponse.From(await s.StartAsync(r, ct), user));
        projects.MapPost("/time/{id:guid}/stop", async (Guid id, TimeService s, ClaimsPrincipal user, CancellationToken ct) =>
            TimeEntryResponse.From(await s.StopAsync(id, ct), user));

        projects.MapGet("/mileage", async (DateOnly? from, DateOnly? to, Guid? customerId, MileageService s, CancellationToken ct) =>
            (await s.ListAsync(from, to, customerId, ct)).Select(MileageTripResponse.From));
        projects.MapPost("/mileage", async (MileageDetails r, MileageService s, CancellationToken ct) =>
        {
            var trip = await s.CreateAsync(r, ct);
            return Results.Created($"/api/projects/mileage/{trip.Id}", MileageTripResponse.From(trip));
        });
        projects.MapPut("/mileage/{id:guid}", async (Guid id, MileageDetails r, MileageService s, CancellationToken ct) =>
            MileageTripResponse.From(await s.UpdateAsync(id, r, ct)));
        projects.MapDelete("/mileage/{id:guid}", async (Guid id, MileageService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        projects.MapGet("/rates", async (BillingRateService s, CancellationToken ct) => await s.ListAsync(ct));
        projects.MapPost("/rates/raise", async (RaiseRatesRequest r, BillingRateService s, CancellationToken ct) =>
            new { Added = await s.RaiseAsync(r.Percent, r.From, r.WholeUnits, ct) });
        projects.MapPost("/time/reprice", async (RepriceTimeRequest r, TimeService s, CancellationToken ct) =>
            new { Repriced = await s.RerateUnbilledAsync(r.From, r.To, r.CustomerId, ct) });
        projects.MapPost("/rates", async (BillingRateDetails r, BillingRateService s, CancellationToken ct) =>
        {
            var rate = await s.CreateAsync(r, ct);
            return Results.Created($"/api/projects/rates/{rate.Id}", rate);
        });
        projects.MapPut("/rates/{id:guid}", async (Guid id, BillingRateDetails r, BillingRateService s, CancellationToken ct) => await s.UpdateAsync(id, r, ct));
        projects.MapDelete("/rates/{id:guid}", async (Guid id, BillingRateService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        // The rate time would get from the rate card, and where it comes from.
        projects.MapGet("/time/rate", async (Guid workerId, Guid? customerId, Guid? itemId, DateOnly? date, TimeService s, CancellationToken ct) =>
            await s.QuoteRateAsync(workerId, customerId, itemId, date ?? CompanyTime.Today, ct));

        projects.MapGet("/mileage-rates", async (MileageService s, CancellationToken ct) =>
            (await s.RatesAsync(ct)).Select(kv => new MileageRateRequest(kv.Key, kv.Value)));
        projects.MapPut("/mileage-rates", async (MileageRateRequest r, MileageService s, CancellationToken ct) =>
        {
            await s.SetRateAsync(r.Year, r.Rate, ct);
            return Results.NoContent();
        });
    }
}

public sealed record ResponsibleRequest(Guid? WorkerId);
