using OpenBook.Domain.Common;

namespace OpenBook.Domain.Projects;

public enum ProjectStatus
{
    NotStarted,
    InProgress,
    Completed,
    Cancelled,
}

/// <param name="BudgetRevenue">What the project should bring in (contract value).</param>
/// <param name="BudgetCost">What it should cost: expenses, labor and mileage.</param>
public sealed record ProjectDetails(
    ProjectStatus Status = ProjectStatus.InProgress,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    decimal? BudgetRevenue = null,
    decimal? BudgetCost = null,
    decimal? BudgetHours = null,
    string? Description = null);

/// <summary>
/// A job done for a customer, tracked for budget and profit. A project is a sub-customer (its own customer record,
/// usually billed with its parent), so its invoices, receipts and credit memos are the project's income without
/// anything new on those documents; this row adds the status, dates and budgets.
/// </summary>
public sealed class Project
{
    private Project() { }

    public Guid Id { get; private set; }

    /// <summary>The project's own customer record (one project per customer).</summary>
    public Guid CustomerId { get; private set; }

    public ProjectStatus Status { get; private set; }
    public DateOnly? StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public decimal? BudgetRevenue { get; private set; }
    public decimal? BudgetCost { get; private set; }
    public decimal? BudgetHours { get; private set; }
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsOpen => Status is ProjectStatus.NotStarted or ProjectStatus.InProgress;

    public static Project Create(Guid customerId, ProjectDetails details, DateTimeOffset createdAt)
    {
        var project = new Project { Id = Guid.CreateVersion7(), CustomerId = customerId, CreatedAt = createdAt.ToUniversalTime() };
        project.Update(details);
        return project;
    }

    public void Update(ProjectDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!Enum.IsDefined(details.Status))
            throw new DomainException("project.status_invalid", "Choose a status.");
        if (details is { StartDate: { } start, EndDate: { } end } && end < start)
            throw new DomainException("project.dates_invalid", "The end date can't be before the start date.");
        Budget(details.BudgetRevenue, "Revenue budget");
        Budget(details.BudgetCost, "Cost budget");
        if (details.BudgetHours is { } hours && (hours < 0m || decimal.Round(hours, 2) != hours))
            throw new DomainException("project.budget_invalid", "Budgeted hours can't be negative and have at most 2 decimals.");
        Status = details.Status;
        StartDate = details.StartDate;
        EndDate = details.EndDate;
        BudgetRevenue = details.BudgetRevenue;
        BudgetCost = details.BudgetCost;
        BudgetHours = details.BudgetHours;
        Description = Text.Optional(details.Description, 2000, "project.description_too_long", "Description");
    }

    private static void Budget(decimal? amount, string name)
    {
        if (amount is not { } a)
            return;
        Money.EnsureCents(a);
        if (a < 0m)
            throw new DomainException("project.budget_invalid", $"{name} can't be negative.");
    }
}

/// <param name="CostRate">What an hour of their time costs the business (wages and burden, or a contractor's rate).</param>
/// <param name="BillRate">Their default hourly rate to customers; null uses the service's price.</param>
/// <param name="Username">The OpenBook user who is this person, so their timer and time sheet know them.</param>
/// <param name="Role">Their role for billing ("Partner", "Associate"): rate card lines can be for a role.</param>
/// <param name="WeeklyHours">Hours they're available a week, for the utilization report; null leaves them out of it.</param>
public sealed record WorkerDetails(
    string Name, decimal CostRate = 0m, decimal? BillRate = null, string? Username = null, Guid? VendorId = null, string? Role = null, decimal? WeeklyHours = null);

/// <summary>Someone whose time is tracked: an employee, an owner or a contractor (optionally linked to their vendor record).</summary>
public sealed class Worker
{
    private Worker() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public decimal CostRate { get; private set; }
    public decimal? BillRate { get; private set; }
    public const int MaxRoleLength = 50;

    public string? Username { get; private set; }
    public Guid? VendorId { get; private set; }
    public string? Role { get; private set; }
    public decimal? WeeklyHours { get; private set; }
    public bool IsActive { get; private set; }

    public static Worker Create(WorkerDetails details)
    {
        var worker = new Worker { Id = Guid.CreateVersion7(), IsActive = true };
        worker.Update(details);
        return worker;
    }

    public void Update(WorkerDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = Text.Optional(details.Name, 100, "worker.name_too_long", "Name")
            ?? throw new DomainException("worker.name_required", "Enter the person's name.");
        TimeEntry.CheckRate(details.CostRate, "Cost rate");
        if (details.BillRate is { } bill)
            TimeEntry.CheckRate(bill, "Bill rate");
        CostRate = details.CostRate;
        BillRate = details.BillRate;
        if (details.WeeklyHours is { } weekly && (weekly <= 0m || weekly > 168m || decimal.Round(weekly, 2) != weekly))
            throw new DomainException("worker.weekly_hours_invalid", "Hours a week are above 0 and at most 168, with at most 2 decimals.");
        Username = Text.Optional(details.Username, 100, "worker.username_too_long", "User");
        VendorId = details.VendorId;
        Role = Text.Optional(details.Role, MaxRoleLength, "worker.role_too_long", "Role");
        WeeklyHours = details.WeeklyHours;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

/// <param name="CustomerId">The customer or project the time is for; required when billable.</param>
/// <param name="ItemId">The service done; required when billable (it's the invoice line's item).</param>
/// <param name="BillRate">Per hour; null takes the worker's bill rate, else the service's price.</param>
public sealed record TimeEntryDetails(
    Guid WorkerId,
    DateOnly Date,
    decimal Hours,
    Guid? CustomerId = null,
    Guid? ItemId = null,
    bool Billable = false,
    decimal? BillRate = null,
    string? Notes = null);

/// <summary>
/// Hours someone worked, optionally for a customer or project and billable at an hourly rate. Time posts nothing:
/// billable time becomes invoice lines (the invoice posts the income), and its cost (hours × the worker's cost rate
/// when entered) counts toward project profitability. A running timer is an entry with <see cref="StartedAt"/> set
/// and no hours yet. Entries can be changed or deleted until they're invoiced.
/// </summary>
public sealed class TimeEntry
{
    public const decimal MaxHours = 24m;

    private TimeEntry() { }

    public Guid Id { get; private set; }
    public Guid WorkerId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Hours { get; private set; }
    public Guid? CustomerId { get; private set; }
    public Guid? ItemId { get; private set; }
    public bool Billable { get; private set; }

    /// <summary>Per hour charged to the customer (0 when not billable).</summary>
    public decimal BillRate { get; private set; }

    /// <summary>Where the bill rate came from ("Acme · Partner", "Entered"); null when not billable.</summary>
    public string? RateSource { get; private set; }

    /// <summary>The worker's cost rate when the time was entered.</summary>
    public decimal CostRate { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>When a running timer started; null once stopped (or for time entered by hand).</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    public Guid? InvoiceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsRunning => StartedAt is not null;

    public decimal Amount => Billable ? decimal.Round(Hours * BillRate, 2, MidpointRounding.AwayFromZero) : 0m;

    public decimal Cost => decimal.Round(Hours * CostRate, 2, MidpointRounding.AwayFromZero);

    public static TimeEntry Create(TimeEntryDetails details, decimal billRate, decimal costRate, DateTimeOffset createdAt, string? rateSource = null)
    {
        var entry = new TimeEntry { Id = Guid.CreateVersion7(), CreatedAt = createdAt.ToUniversalTime() };
        entry.Set(details, billRate, costRate, running: false, rateSource);
        return entry;
    }

    /// <summary>A running timer: hours are filled in by <see cref="Stop"/>.</summary>
    public static TimeEntry Start(TimeEntryDetails details, decimal billRate, decimal costRate, DateTimeOffset startedAt, string? rateSource = null)
    {
        var entry = new TimeEntry { Id = Guid.CreateVersion7(), CreatedAt = startedAt.ToUniversalTime() };
        entry.Set(details with { Hours = 0m }, billRate, costRate, running: true, rateSource);
        entry.StartedAt = startedAt.ToUniversalTime();
        return entry;
    }

    /// <summary>Stops the timer: the hours are the minutes elapsed (at least one), to two decimals of an hour.</summary>
    public void Stop(DateTimeOffset now)
    {
        if (StartedAt is not { } started)
            throw new DomainException("time.not_running", "That timer isn't running.");
        var minutes = Math.Max(1m, Math.Round((decimal)(now - started).TotalMinutes, MidpointRounding.AwayFromZero));
        Hours = Math.Min(MaxHours, decimal.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero));
        StartedAt = null;
    }

    public void Update(TimeEntryDetails details, decimal billRate, decimal costRate, string? rateSource = null)
    {
        EnsureEditable();
        Set(details, billRate, costRate, running: IsRunning, rateSource);
    }

    public void EnsureEditable()
    {
        if (InvoiceId is not null)
            throw new DomainException("time.invoiced", "This time is on an invoice. Void the invoice first.");
    }

    public void MarkInvoiced(Guid invoiceId)
    {
        if (!Billable || IsRunning)
            throw new DomainException("time.not_billable", "Only stopped, billable time can be invoiced.");
        if (InvoiceId is not null)
            throw new DomainException("time.already_invoiced", "That time is already on an invoice.");
        InvoiceId = invoiceId;
    }

    public void ClearInvoice() => InvoiceId = null;

    internal static void CheckRate(decimal rate, string name)
    {
        if (rate < 0m || rate > 100_000m || decimal.Round(rate, 4) != rate)
            throw new DomainException("time.rate_invalid", $"{name} is an amount from 0 with at most 4 decimals.");
    }

    private void Set(TimeEntryDetails details, decimal billRate, decimal costRate, bool running, string? rateSource)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!running && (details.Hours <= 0m || details.Hours > MaxHours || decimal.Round(details.Hours, 2) != details.Hours))
            throw new DomainException("time.hours_invalid", "Hours are above 0 and at most 24, with at most 2 decimals.");
        if (details.Billable && (details.CustomerId is null || details.ItemId is null))
            throw new DomainException("time.billable_needs_customer", "Billable time needs a customer and a service.");
        CheckRate(billRate, "Bill rate");
        CheckRate(costRate, "Cost rate");
        WorkerId = details.WorkerId;
        Date = details.Date;
        Hours = details.Hours;
        CustomerId = details.CustomerId;
        ItemId = details.ItemId;
        Billable = details.Billable;
        BillRate = details.Billable ? billRate : 0m;
        RateSource = details.Billable && rateSource is { Length: > 0 } source ? (source.Length > 100 ? source[..100] : source) : null;
        CostRate = costRate;
        Notes = Text.Optional(details.Notes, 1000, "time.notes_too_long", "Notes");
    }
}

/// <param name="BillRate">Per mile charged to the customer when billable.</param>
public sealed record MileageDetails(
    DateOnly Date,
    decimal Miles,
    string Purpose,
    Guid? WorkerId = null,
    string? Vehicle = null,
    string? FromPlace = null,
    string? ToPlace = null,
    Guid? CustomerId = null,
    bool Billable = false,
    decimal BillRate = 0m);

/// <summary>
/// A business trip by car, for the mileage log (deductible at the standard rate for its year) and, when billable,
/// for charging a customer per mile on an invoice. Posts nothing.
/// </summary>
public sealed class MileageTrip
{
    private MileageTrip() => Purpose = string.Empty;

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Miles { get; private set; }
    public string Purpose { get; private set; }
    public Guid? WorkerId { get; private set; }
    public string? Vehicle { get; private set; }
    public string? FromPlace { get; private set; }
    public string? ToPlace { get; private set; }
    public Guid? CustomerId { get; private set; }
    public bool Billable { get; private set; }
    public decimal BillRate { get; private set; }
    public Guid? InvoiceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public decimal Amount => Billable ? decimal.Round(Miles * BillRate, 2, MidpointRounding.AwayFromZero) : 0m;

    public static MileageTrip Create(MileageDetails details, DateTimeOffset createdAt)
    {
        var trip = new MileageTrip { Id = Guid.CreateVersion7(), CreatedAt = createdAt.ToUniversalTime() };
        trip.Update(details);
        return trip;
    }

    public void Update(MileageDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (InvoiceId is not null)
            throw new DomainException("mileage.invoiced", "This trip is on an invoice. Void the invoice first.");
        if (details.Miles <= 0m || details.Miles > 10_000m || decimal.Round(details.Miles, 1) != details.Miles)
            throw new DomainException("mileage.miles_invalid", "Miles are above 0, with at most one decimal.");
        if (details.Billable && details.CustomerId is null)
            throw new DomainException("mileage.billable_needs_customer", "Billable mileage needs a customer.");
        TimeEntry.CheckRate(details.BillRate, "Rate per mile");
        Date = details.Date;
        Miles = details.Miles;
        Purpose = Text.Optional(details.Purpose, 500, "mileage.purpose_too_long", "Purpose")
            ?? throw new DomainException("mileage.purpose_required", "Enter the business purpose of the trip.");
        WorkerId = details.WorkerId;
        Vehicle = Text.Optional(details.Vehicle, 100, "mileage.vehicle_too_long", "Vehicle");
        FromPlace = Text.Optional(details.FromPlace, 200, "mileage.place_too_long", "From");
        ToPlace = Text.Optional(details.ToPlace, 200, "mileage.place_too_long", "To");
        CustomerId = details.CustomerId;
        Billable = details.Billable;
        BillRate = details.Billable ? details.BillRate : 0m;
    }

    public void MarkInvoiced(Guid invoiceId)
    {
        if (!Billable)
            throw new DomainException("mileage.not_billable", "Only billable mileage can be invoiced.");
        if (InvoiceId is not null)
            throw new DomainException("mileage.already_invoiced", "That trip is already on an invoice.");
        InvoiceId = invoiceId;
    }

    public void ClearInvoice() => InvoiceId = null;
}

/// <summary>The standard mileage rate for business use of a car in a calendar year, per mile.</summary>
public sealed class MileageRate
{
    private MileageRate() { }

    public int Year { get; private set; }
    public decimal Rate { get; private set; }

    /// <summary>IRS business standard mileage rates; years not listed (or overridden) are entered by the user.</summary>
    public static readonly IReadOnlyDictionary<int, decimal> Standard = new Dictionary<int, decimal>
    {
        [2023] = 0.655m,
        [2024] = 0.67m,
        [2025] = 0.70m,
        [2026] = 0.725m,
    };

    /// <summary>The year's rate, else the latest before it: a new year's trips shouldn't cost nothing until its rate is announced.</summary>
    public static decimal For(IReadOnlyDictionary<int, decimal> rates, int year)
    {
        ArgumentNullException.ThrowIfNull(rates);
        return rates.TryGetValue(year, out var rate) ? rate : rates.Where(r => r.Key < year).OrderByDescending(r => r.Key).Select(r => r.Value).FirstOrDefault();
    }

    public static MileageRate Create(int year, decimal rate)
    {
        var r = new MileageRate { Year = year };
        r.Set(rate);
        return r;
    }

    public void Set(decimal rate)
    {
        if (Year is < 2000 or > 2100)
            throw new DomainException("mileage.year_invalid", "Enter a year from 2000 to 2100.");
        if (rate < 0m || rate > 10m || decimal.Round(rate, 4) != rate)
            throw new DomainException("mileage.rate_invalid", "The rate is an amount per mile from 0 to 10, with at most 4 decimals.");
        Rate = rate;
    }
}
