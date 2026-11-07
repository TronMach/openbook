using System.Globalization;
using OpenBook.Application.Projects;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the project, time, people and mileage pages (antiforgery-checked, form-bound).</summary>
public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects", async (IFormCollection form, ProjectService projects, CancellationToken ct) =>
        {
            try
            {
                var project = await projects.CreateAsync(new CreateProjectCommand(Details(form), Text(form["name"]), Id(form["parentId"]), Id(form["customerId"])), ct);
                return Results.Redirect($"/projects/{project.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/projects?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/projects/{id:guid}", async (Guid id, IFormCollection form, ProjectService projects, CancellationToken ct) =>
            await Back($"/projects/{id}", async () => await projects.UpdateAsync(id, Details(form), ct))).RequireAuthorization();

        // Fields: workerId, week (any day of it), and per row r: c:r, i:r, b:r and h:r:d (d = 0 for Monday … 6). A posted
        // empty cell means zero; a disabled (locked) cell isn't posted, so it's left alone.
        app.MapPost("/time/week", async (IFormCollection form, TimeService time, CancellationToken ct) =>
        {
            var workerId = Guid.TryParse(form["workerId"], out var w) ? w : Guid.Empty;
            var day = DateOnly.TryParseExact(form["week"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : CompanyTime.Today;
            var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
            var back = $"/time/week?worker={workerId}&week={monday:yyyy-MM-dd}";
            var rowIndexes = form.Keys.Where(k => k.StartsWith("h:", StringComparison.Ordinal)).Select(k => k.Split(':')[1]).Distinct();
            var rows = new List<WeekRow>();
            foreach (var r in rowIndexes)
            {
                var hours = new List<decimal?>();
                var isNew = form[$"new:{r}"] == "true"; // emptying a cell of a saved row clears it; a new row's blanks say nothing
                for (var i = 0; i < 7; i++)
                {
                    var key = $"h:{r}:{i}";
                    if (!form.ContainsKey(key))
                        hours.Add(null);
                    else if (form[key].ToString().Trim() is { Length: 0 })
                        hours.Add(isNew ? null : 0m);
                    else if (decimal.TryParse(form[key], NumberStyles.Number, CultureInfo.InvariantCulture, out var h))
                        hours.Add(h);
                    else
                        return Results.Redirect($"{back}&error={Uri.EscapeDataString($"'{form[key]}' isn't a number of hours.")}");
                }

                rows.Add(new WeekRow(Guid.TryParse(form[$"c:{r}"], out var c) ? c : null, Guid.TryParse(form[$"i:{r}"], out var item) ? item : null,
                    form[$"b:{r}"] == "true", hours));
            }

            try
            {
                var changed = await time.SaveWeekAsync(workerId, monday, rows, ct);
                return Results.Redirect($"{back}&saved={changed}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/time", async (IFormCollection form, TimeService time, CancellationToken ct) =>
        {
            var back = Id(form["customerId"]) is { } c ? $"/time?customer={c}" : "/time";
            return await Back(back, async () =>
            {
                if (form["mode"] == "start")
                    await time.StartAsync(TimeDetails(form, requireHours: false), ct);
                else
                    await time.CreateAsync(TimeDetails(form, requireHours: true), ct);
            });
        }).RequireAuthorization();

        app.MapPost("/time/{id:guid}", async (Guid id, IFormCollection form, TimeService time, CancellationToken ct) =>
        {
            try
            {
                var existing = await time.GetAsync(id, ct) ?? throw new EntityNotFoundException(TimeService.EntityType, id);
                await time.UpdateAsync(id, TimeDetails(form, requireHours: !existing.IsRunning), ct);
                return Results.Redirect("/time");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/time/{id}/edit?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/time/{id:guid}/stop", async (IFormCollection _, Guid id, TimeService time, CancellationToken ct) =>
            await Back("/time", async () => await time.StopAsync(id, ct))).RequireAuthorization();

        app.MapPost("/time/{id:guid}/delete", async (IFormCollection _, Guid id, TimeService time, CancellationToken ct) =>
            await Back("/time", () => time.DeleteAsync(id, ct))).RequireAuthorization();

        app.MapPost("/time/people", async (IFormCollection form, WorkerService workers, CancellationToken ct) =>
            await Back("/time/people", async () => await workers.CreateAsync(WorkerDetails(form), ct))).RequireAuthorization();

        app.MapPost("/time/people/{id:guid}", async (Guid id, IFormCollection form, WorkerService workers, CancellationToken ct) =>
        {
            try
            {
                await workers.UpdateAsync(id, WorkerDetails(form), ct);
                await workers.SetActiveAsync(id, form["active"] == "true", ct);
                return Results.Redirect("/time/people");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/time/people?edit={id}&error={Uri.EscapeDataString(ex.Message)}#edit");
            }
        }).RequireAuthorization();

        app.MapPost("/mileage", async (IFormCollection form, MileageService mileage, CancellationToken ct) =>
            await Back("/mileage", async () => await mileage.CreateAsync(new MileageDetails(
                Date(form["date"]) ?? Today(),
                Number(form["miles"]) ?? throw new DomainException("mileage.miles_invalid", "Enter the miles driven."),
                form["purpose"].ToString(),
                Id(form["workerId"]),
                Text(form["vehicle"]),
                Text(form["fromPlace"]),
                Text(form["toPlace"]),
                Id(form["customerId"]),
                form["billable"] == "true",
                Number(form["billRate"]) ?? 0m), ct))).RequireAuthorization();

        app.MapPost("/mileage/{id:guid}/delete", async (IFormCollection _, Guid id, MileageService mileage, CancellationToken ct) =>
            await Back("/mileage", () => mileage.DeleteAsync(id, ct))).RequireAuthorization();

        app.MapPost("/time/rates", async (IFormCollection form, BillingRateService rates, CancellationToken ct) =>
            await Back("/time/rates", async () => await rates.CreateAsync(RateDetails(form), ct))).RequireAuthorization();

        app.MapPost("/time/rates/{id:guid}", async (Guid id, IFormCollection form, BillingRateService rates, CancellationToken ct) =>
            await Back("/time/rates", async () => await rates.UpdateAsync(id, RateDetails(form), ct))).RequireAuthorization();

        app.MapPost("/time/rates/{id:guid}/delete", async (IFormCollection _, Guid id, BillingRateService rates, CancellationToken ct) =>
            await Back("/time/rates", () => rates.DeleteAsync(id, ct))).RequireAuthorization();

        app.MapPost("/mileage/rates", async (IFormCollection form, MileageService mileage, CancellationToken ct) =>
            await Back("/mileage", () => mileage.SetRateAsync(
                int.TryParse(form["year"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : 0,
                Number(form["rate"]) ?? throw new DomainException("mileage.rate_invalid", "Enter the rate per mile."), ct))).RequireAuthorization();

        // The billing run.
        app.MapPost("/time/billing/drafts", async (IFormCollection form, PreBillService billing, CancellationToken ct) =>
        {
            var through = Date(form["through"]) ?? Today();
            List<Guid> clients = [.. form["client"].Select(c => Guid.TryParse(c, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)];
            try
            {
                var drafts = await billing.MakeDraftsAsync(clients, through, ct);
                return Results.Redirect($"/time/billing?through={through:yyyy-MM-dd}&note={Uri.EscapeDataString($"{drafts.Count} draft(s) made.")}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/time/billing?through={through:yyyy-MM-dd}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/time/billing/post", async (IFormCollection form, PreBillService billing, CancellationToken ct) =>
        {
            List<Guid> drafts = [.. form["draft"].Select(c => Guid.TryParse(c, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)];
            var run = await billing.PostManyAsync(drafts, Date(form["date"]) ?? Today(), form["send"] == "true", ct);
            var note = $"{run.Posted} posted, {run.Emailed} emailed.{(run.Problems.Count == 0 ? "" : " " + string.Join(" ", run.Problems))}";
            return Results.Redirect($"/time/billing?note={Uri.EscapeDataString(note)}");
        }).RequireAuthorization();

        app.MapPost("/time/billing/{id:guid}", async (Guid id, IFormCollection form, PreBillService billing, CancellationToken ct) =>
        {
            try
            {
                await SaveDraftAsync(billing, id, form, ct);
                return Results.Redirect($"/time/billing/{id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/time/billing/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/time/billing/{id:guid}/post", async (Guid id, IFormCollection form, PreBillService billing, CancellationToken ct) =>
        {
            try
            {
                await SaveDraftAsync(billing, id, form, ct);
                var invoice = await billing.PostAsync(id, Today(), ct);
                return Results.Redirect(invoice is null ? "/time/billing" : $"/sales/invoices/{invoice.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/time/billing/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/time/billing/{id:guid}/discard", async (Guid id, IFormCollection form, PreBillService billing, CancellationToken ct) =>
        {
            await billing.DiscardAsync(id, ct);
            return Results.Redirect("/time/billing");
        }).RequireAuthorization();

        return app;
    }

    private static async Task SaveDraftAsync(PreBillService billing, Guid id, IFormCollection form, CancellationToken ct)
    {
        var removed = form["remove"].Select(r => int.TryParse(r, out var line) ? line : 0).ToHashSet();
        var changes = new List<PreBillLineChange>(removed.Where(l => l > 0).Select(l => new PreBillLineChange(l, 0m, null, Remove: true)));
        foreach (var key in form.Keys.Where(k => k.StartsWith("billed:", StringComparison.Ordinal)))
        {
            if (int.TryParse(key["billed:".Length..], out var line) && !removed.Contains(line) && Number(form[key]) is { } billed)
                changes.Add(new PreBillLineChange(line, billed, form[$"reason:{line}"].ToString()));
        }

        await billing.SaveAsync(id, form["memo"].ToString(), changes, ct);
    }

    private static ProjectDetails Details(IFormCollection form) => new(
        Enum.TryParse<ProjectStatus>(form["status"], out var status) ? status : ProjectStatus.InProgress,
        Date(form["startDate"]),
        Date(form["endDate"]),
        Number(form["budgetRevenue"]),
        Number(form["budgetCost"]),
        Number(form["budgetHours"]),
        Text(form["description"]),
        Enum.TryParse<BillingArrangement>(form["arrangement"], out var arrangement) ? arrangement : BillingArrangement.TimeAndMaterials,
        Number(form["fee"]));

    private static TimeEntryDetails TimeDetails(IFormCollection form, bool requireHours) => new(
        Id(form["workerId"]) ?? throw new DomainException("time.worker_required", "Choose who did the work."),
        Date(form["date"]) ?? Today(),
        requireHours ? Number(form["hours"]) ?? throw new DomainException("time.hours_invalid", "Enter the hours worked.") : 0m,
        Id(form["customerId"]),
        Id(form["itemId"]),
        form["billable"] == "true",
        Number(form["rate"]),
        Text(form["notes"]));

    private static WorkerDetails WorkerDetails(IFormCollection form) => new(
        form["name"].ToString(),
        Number(form["costRate"]) ?? 0m,
        Number(form["billRate"]),
        Text(form["username"]),
        Id(form["vendorId"]),
        Text(form["role"]),
        Number(form["weeklyHours"]));

    /// <summary>The "for" field is one choice: worker:{id}, role:{name}, item:{id}, or blank for the client alone.</summary>
    private static BillingRateDetails RateDetails(IFormCollection form)
    {
        var target = form["for"].ToString();
        var (kind, value) = target.IndexOf(':', StringComparison.Ordinal) is var i and > 0 ? (target[..i], target[(i + 1)..]) : ("", "");
        return new BillingRateDetails(
            Number(form["rate"]) ?? throw new DomainException("rate.rate_required", "Enter the hourly rate."),
            Id(form["customerId"]),
            kind == "worker" ? Id(value) : null,
            kind == "role" ? Text(value) : null,
            kind == "item" ? Id(value) : null,
            Date(form["startsOn"]));
    }

    private static async Task<IResult> Back(string page, Func<Task> change)
    {
        var separator = page.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        try
        {
            await change();
            return Results.Redirect(page);
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}{separator}error={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>A plain number (hours, miles, rates): digits with an optional decimal point, commas as thousands separators.</summary>
    private static decimal? Number(string? value) => FormNumbers.Number(value);

    private static DateOnly Today() => CompanyTime.Today;
}
