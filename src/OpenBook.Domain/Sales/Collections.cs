using System.Globalization;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>
/// How the company chases overdue invoices: late fees (a percentage of the overdue balance and/or a flat amount,
/// after a grace period, once or repeating) and payment reminder emails on a schedule around the due date.
/// One row per company; created with everything off.
/// </summary>
public sealed class CollectionSettings
{
    public const int MaxReminderSteps = 6;

    public const string DefaultSubject = "Reminder: invoice {invoice} from {company}";

    public const string DefaultBody =
        "Hello {customer},\n\nThis is a reminder that invoice {invoice} for {balance} {when}. A copy is attached.\n\n"
        + "If you've already paid, thank you, and please ignore this message.\n\n{company}";

    private CollectionSettings()
    {
        ReminderSchedule = "";
        ReminderSubject = DefaultSubject;
        ReminderBody = DefaultBody;
    }

    public Guid Id { get; private set; }

    /// <summary>Show late fees as due on the collections page.</summary>
    public bool LateFeesEnabled { get; private set; }

    /// <summary>Charge due late fees without anyone pressing the button (checked every hour).</summary>
    public bool LateFeesAutomatic { get; private set; }

    /// <summary>Percent of the invoice's overdue balance, to 4 decimals.</summary>
    public decimal LateFeePercent { get; private set; }

    public decimal LateFeeAmount { get; private set; }

    /// <summary>Days after the due date before a fee is charged.</summary>
    public int LateFeeGraceDays { get; private set; }

    /// <summary>Charge again every this many days while the invoice stays unpaid; 0 charges once.</summary>
    public int LateFeeRepeatDays { get; private set; }

    /// <summary>The service item fee invoices use; created ("Late Fee") on the first charge.</summary>
    public Guid? LateFeeItemId { get; private set; }

    /// <summary>Send due reminders automatically (checked every hour).</summary>
    public bool RemindersEnabled { get; private set; }

    /// <summary>Days relative to the due date, comma separated ("-3,7,30"): negative is before it.</summary>
    public string ReminderSchedule { get; private set; }

    /// <summary>With placeholders {customer}, {invoice}, {balance}, {due}, {days}, {when} and {company}.</summary>
    public string ReminderSubject { get; private set; }

    public string ReminderBody { get; private set; }

    public IReadOnlyList<int> ReminderSteps => ParseSchedule(ReminderSchedule);

    public static CollectionSettings Create() => new() { Id = Guid.CreateVersion7(), ReminderSchedule = "-3,7,30" };

    public void UpdateLateFees(bool enabled, bool automatic, decimal percent, decimal amount, int graceDays, int repeatDays)
    {
        if (percent is < 0m or > 100m || decimal.Round(percent, 4) != percent)
            throw new DomainException("latefee.percent_invalid", "The late fee percentage is from 0 to 100, with at most 4 decimals.");
        Money.EnsureCents(amount);
        if (amount < 0m)
            throw new DomainException("latefee.amount_invalid", "The flat late fee can't be negative.");
        if (enabled && percent == 0m && amount == 0m)
            throw new DomainException("latefee.nothing_to_charge", "Enter a percentage, a flat amount or both.");
        if (graceDays is < 0 or > 365)
            throw new DomainException("latefee.grace_invalid", "The grace period is 0 to 365 days.");
        if (repeatDays is < 0 or > 365)
            throw new DomainException("latefee.repeat_invalid", "Repeat every 1 to 365 days, or 0 to charge once.");
        LateFeesEnabled = enabled;
        LateFeesAutomatic = enabled && automatic;
        LateFeePercent = percent;
        LateFeeAmount = amount;
        LateFeeGraceDays = graceDays;
        LateFeeRepeatDays = repeatDays;
    }

    public void SetLateFeeItem(Guid itemId) => LateFeeItemId = itemId;

    public void UpdateReminders(bool enabled, string? schedule, string? subject, string? body)
    {
        var steps = ParseSchedule(schedule);
        if (enabled && steps.Count == 0)
            throw new DomainException("reminder.schedule_required", "Enter when to send reminders, in days from the due date (for example -3, 7, 30).");
        Email.EmailTemplates.Check(Email.EmailKind.Reminder, subject, body);
        RemindersEnabled = enabled;
        ReminderSchedule = string.Join(",", steps.Select(s => s.ToString(CultureInfo.InvariantCulture)));
        ReminderSubject = Text.Optional(subject, 300, "reminder.subject_too_long", "Subject") ?? DefaultSubject;
        ReminderBody = Text.Optional(body, 4000, "reminder.body_too_long", "Message") ?? DefaultBody;
    }

    /// <summary>The fee for an invoice with this much overdue: percentage plus flat amount, to the cent.</summary>
    public decimal FeeFor(decimal overdueBalance) =>
        decimal.Round(overdueBalance * LateFeePercent / 100m, 2, MidpointRounding.AwayFromZero) + LateFeeAmount;

    /// <summary>
    /// Whether a fee is due on <paramref name="today"/> for an invoice with this due date, given when the last
    /// fee on it was charged (null for never).
    /// </summary>
    public bool FeeDue(DateOnly dueDate, DateOnly? lastFee, DateOnly today) =>
        today > dueDate.AddDays(LateFeeGraceDays)
        && (lastFee is not { } last || (LateFeeRepeatDays > 0 && today >= last.AddDays(LateFeeRepeatDays)));

    /// <summary>
    /// The schedule step a reminder is due for on <paramref name="today"/>: the latest step reached that falls on or
    /// after the invoice date and is later than any already sent; null when none is due.
    /// </summary>
    public int? ReminderDue(DateOnly invoiceDate, DateOnly dueDate, int? lastSentStep, DateOnly today)
    {
        int? due = null;
        foreach (var step in ReminderSteps)
        {
            var on = dueDate.AddDays(step);
            if (on <= today && on >= invoiceDate && (lastSentStep is not { } sent || step > sent))
                due = step;
        }

        return due;
    }

    /// <summary>Fills the placeholders in a reminder subject or body.</summary>
    public static string Fill(string template, string customer, string invoice, decimal balance, DateOnly due, DateOnly today, string company)
    {
        ArgumentNullException.ThrowIfNull(template);
        var days = today.DayNumber - due.DayNumber;
        var dueText = due.ToString("d", CultureInfo.CurrentCulture);
        var when = days switch
        {
            < 0 => $"is due on {dueText}",
            0 => "is due today",
            1 => $"was due on {dueText}, 1 day ago",
            _ => $"was due on {dueText}, {days} days ago",
        };
        return template
            .Replace("{customer}", customer, StringComparison.Ordinal)
            .Replace("{invoice}", invoice, StringComparison.Ordinal)
            .Replace("{balance}", balance.ToString("N2", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{due}", dueText, StringComparison.Ordinal)
            .Replace("{days}", Math.Max(0, days).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{when}", when, StringComparison.Ordinal)
            .Replace("{company}", company, StringComparison.Ordinal);
    }

    /// <summary>"-3, 7,30" → [-3, 7, 30]: whole days from -90 to 365, distinct, sorted, at most <see cref="MaxReminderSteps"/>.</summary>
    public static IReadOnlyList<int> ParseSchedule(string? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            return [];
        var steps = new SortedSet<int>();
        foreach (var part in schedule.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var day) || day is < -90 or > 365)
                throw new DomainException("reminder.schedule_invalid", $"'{part}' isn't a number of days from -90 to 365.");
            steps.Add(day);
        }

        if (steps.Count > MaxReminderSteps)
            throw new DomainException("reminder.schedule_too_long", $"Send at most {MaxReminderSteps} reminders per invoice.");
        return [.. steps];
    }
}

/// <summary>A payment reminder emailed for an invoice, for one step of the schedule.</summary>
public sealed class InvoiceReminder
{
    private InvoiceReminder() => To = string.Empty;

    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }

    /// <summary>Days from the due date this reminder was for.</summary>
    public int Step { get; private set; }

    public DateTimeOffset SentAt { get; private set; }
    public string To { get; private set; }

    public static InvoiceReminder Create(Guid invoiceId, int step, DateTimeOffset sentAt, string to) => new()
    {
        Id = Guid.CreateVersion7(),
        InvoiceId = invoiceId,
        Step = step,
        SentAt = sentAt.ToUniversalTime(),
        To = to,
    };
}
