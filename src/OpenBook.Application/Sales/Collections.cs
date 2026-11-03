using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Amount">Null writes off the whole balance.</param>
public sealed record WriteOffLine(Guid InvoiceId, decimal? Amount = null);

/// <param name="ExpenseAccountId">Null uses "Bad Debts" (created on first use).</param>
public sealed record WriteOffCommand(DateOnly Date, IReadOnlyList<WriteOffLine> Invoices, Guid? ExpenseAccountId = null, string? Memo = null);

/// <summary>An open invoice that could be written off, with how long it's been overdue.</summary>
public sealed record WriteOffCandidate(Invoice Invoice, Customer Customer, int DaysPastDue);

/// <summary>
/// Bad debt: writes off what's left of invoices the customer won't pay (Dr bad debt expense, Cr A/R), one
/// document and entry per invoice, all in one save. Voiding one puts the balance back on its invoice.
/// </summary>
public sealed class WriteOffService(ISalesStore store, AccountService accounts, PostingEngine engine, IClock clock, ICurrentUser user)
{
    public const string EntityType = "write_off";
    public const string BadDebtsAccountName = "Bad Debts";

    public Task<BadDebtWriteOff?> GetAsync(Guid id, CancellationToken ct = default) => store.GetWriteOffAsync(id, ct);

    public Task<IReadOnlyList<BadDebtWriteOff>> ListAsync(Guid? customerId = null, CancellationToken ct = default) => store.ListWriteOffsAsync(customerId, ct);

    public async Task<IReadOnlyList<BadDebtWriteOff>> ListForInvoiceAsync(Guid invoiceId, CancellationToken ct = default) =>
        (await store.ListWriteOffsAsync(null, ct)).Where(w => w.InvoiceId == invoiceId).ToList();

    /// <summary>Open invoices at least <paramref name="minDaysPastDue"/> days past due on <paramref name="asOf"/>, oldest due first.</summary>
    public async Task<IReadOnlyList<WriteOffCandidate>> CandidatesAsync(DateOnly asOf, int minDaysPastDue, CancellationToken ct = default)
    {
        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        return (await store.ListOpenInvoicesAsync(null, ct))
            .Where(i => i.Date <= asOf && asOf.DayNumber - i.DueDate.DayNumber >= minDaysPastDue)
            .OrderBy(i => i.DueDate).ThenBy(i => i.Number, StringComparer.Ordinal)
            .Select(i => new WriteOffCandidate(i, customers[i.CustomerId], Math.Max(0, asOf.DayNumber - i.DueDate.DayNumber)))
            .ToList();
    }

    public async Task<IReadOnlyList<BadDebtWriteOff>> WriteOffAsync(WriteOffCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Invoices);
        if (command.Invoices.Count == 0)
            throw new DomainException("writeoff.nothing_chosen", "Choose the invoices to write off.");
        if (command.Invoices.Select(l => l.InvoiceId).Distinct().Count() != command.Invoices.Count)
            throw new DomainException("writeoff.duplicate_invoice", "Each invoice can be written off once per write-off.");

        var invoices = (await store.GetInvoicesAsync(command.Invoices.Select(l => l.InvoiceId).ToList(), ct)).ToDictionary(i => i.Id);
        var lines = new List<(Invoice Invoice, decimal Amount)>();
        foreach (var line in command.Invoices)
        {
            var invoice = invoices.GetValueOrDefault(line.InvoiceId) ?? throw new EntityNotFoundException("invoice", line.InvoiceId);
            var amount = line.Amount ?? invoice.Balance;
            BadDebtWriteOff.Check(invoice, command.Date, amount);
            lines.Add((invoice, amount));
        }

        var expense = command.ExpenseAccountId is { } chosen ? await ExpenseAccountAsync(chosen, ct) : await GetOrCreateBadDebtsAsync(ct);
        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var created = new List<BadDebtWriteOff>();
        foreach (var (invoice, amount) in lines)
        {
            var id = Guid.CreateVersion7();
            var memo = $"Bad debt · invoice {invoice.Number} · {customers[invoice.CustomerId].DisplayName}";
            var entry = await engine.StageAsync(new PostJournalEntryCommand(
                command.Date,
                [JournalLineDraft.Dr(expense.Id, amount), JournalLineDraft.Cr(invoice.ReceivableAccountId, amount)],
                memo,
                BadDebtWriteOff.Source,
                id), ct);
            created.Add(BadDebtWriteOff.Create(id, invoice, command.Date, amount, expense.Id, entry.Id, clock.UtcNow, command.Memo));
        }

        foreach (var writeOff in created)
        {
            var invoice = invoices[writeOff.InvoiceId];
            var before = InvoiceService.Snapshot(invoice);
            invoice.ApplyPayment(writeOff.Amount);
            store.Add(writeOff);
            store.Add(Audit(AuditAction.Created, writeOff, null));
            store.Add(InvoiceService.Audit(clock, user, AuditAction.Updated, invoice, before));
        }

        await store.SaveChangesAsync(ct);
        return created;
    }

    public async Task<BadDebtWriteOff> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var writeOff = await store.GetWriteOffAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (writeOff.Status == DocumentStatus.Voided)
            throw new DomainException("writeoff.already_voided", "This write-off is already voided.");
        var invoice = await store.GetInvoiceAsync(writeOff.InvoiceId, ct) ?? throw new EntityNotFoundException("invoice", writeOff.InvoiceId);

        var before = Snapshot(writeOff);
        await engine.StageVoidAsync(writeOff.JournalEntryId, reason, fromDocument: true, ct);
        writeOff.Void();
        var invoiceBefore = InvoiceService.Snapshot(invoice);
        invoice.RemovePayment(writeOff.Amount);
        store.Add(Audit(AuditAction.Voided, writeOff, before));
        store.Add(InvoiceService.Audit(clock, user, AuditAction.Updated, invoice, invoiceBefore));
        await store.SaveChangesAsync(ct);
        return writeOff;
    }

    /// <summary>The active expense account "Bad Debts", created (and saved) on first use.</summary>
    public async Task<Account> GetOrCreateBadDebtsAsync(CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        var existing = chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type is AccountType.Expense or AccountType.OtherExpense && string.Equals(a.Name, BadDebtsAccountName, StringComparison.OrdinalIgnoreCase));
        return existing ?? await accounts.CreateAsync(new CreateAccountCommand(BadDebtsAccountName, AccountType.Expense,
            Description: "Invoices written off as uncollectible."), ct);
    }

    private async Task<Account> ExpenseAccountAsync(Guid id, CancellationToken ct)
    {
        var account = (await accounts.GetChartAsync(ct)).Get(id);
        if (account.Type is not (AccountType.Expense or AccountType.OtherExpense))
            throw new DomainException("writeoff.account_type", $"'{account.Name}' isn't an expense account.");
        if (!account.IsActive)
            throw new DomainException("writeoff.account_inactive", $"'{account.Name}' is inactive.");
        return account;
    }

    private AuditRecord Audit(AuditAction action, BadDebtWriteOff w, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, w.Id, before, Snapshot(w));

    private static string Snapshot(BadDebtWriteOff w) => JsonSerializer.Serialize(new
    {
        w.CustomerId,
        w.InvoiceId,
        w.Date,
        w.Amount,
        w.ExpenseAccountId,
        w.Memo,
        Status = w.Status.ToString(),
    });
}

public sealed record LateFeeSettingsCommand(bool Enabled, bool Automatic, decimal Percent, decimal Amount, int GraceDays, int RepeatDays);

public sealed record ReminderSettingsCommand(bool Enabled, string? Schedule, string? Subject, string? Body);

/// <summary>The company's late fee and reminder settings (accountant).</summary>
public sealed class CollectionService(ISalesStore store, ICompanyStore companies, IClock clock, ICurrentUser user)
{
    public const string EntityType = "collection_settings";

    /// <summary>The saved settings, or the defaults (everything off) before the first save.</summary>
    public async Task<CollectionSettings> GetAsync(CancellationToken ct = default) =>
        await store.GetCollectionSettingsAsync(ct) ?? CollectionSettings.Create();

    public Task<CollectionSettings> SaveLateFeesAsync(LateFeeSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SaveAsync(s => s.UpdateLateFees(command.Enabled, command.Automatic, command.Percent, command.Amount, command.GraceDays, command.RepeatDays), ct);
    }

    public Task<CollectionSettings> SaveRemindersAsync(ReminderSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SaveAsync(s => s.UpdateReminders(command.Enabled, command.Schedule, command.Subject, command.Body), ct);
    }

    internal async Task<CollectionSettings> EnsureAsync(CancellationToken ct)
    {
        var settings = await store.GetCollectionSettingsAsync(ct);
        if (settings is null)
        {
            settings = CollectionSettings.Create();
            store.Add(settings);
        }

        return settings;
    }

    private async Task<CollectionSettings> SaveAsync(Action<CollectionSettings> change, CancellationToken ct)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var settings = await EnsureAsync(ct);
        var before = Snapshot(settings);
        change(settings);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, company.Id, before, Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    internal static string Snapshot(CollectionSettings s) => JsonSerializer.Serialize(new
    {
        s.LateFeesEnabled,
        s.LateFeesAutomatic,
        s.LateFeePercent,
        s.LateFeeAmount,
        s.LateFeeGraceDays,
        s.LateFeeRepeatDays,
        s.LateFeeItemId,
        s.RemindersEnabled,
        s.ReminderSchedule,
        s.ReminderSubject,
        s.ReminderBody,
    });
}

/// <summary>A late fee due today on an overdue invoice.</summary>
public sealed record LateFeeDue(Invoice Invoice, Customer Customer, int DaysPastDue, DateOnly? LastFee, decimal Fee);

/// <summary>
/// Late fees: each overdue invoice (not itself a fee) past the grace period gets a fee invoice of its own, due on
/// receipt, once or every so many days while it stays unpaid. Customers marked "no late fees" are skipped.
/// </summary>
public sealed class LateFeeService(
    ISalesStore store, ICompanyStore companies, CollectionService collections, InvoiceService invoices, ItemService items, AccountService accounts,
    IClock clock, ICurrentUser user)
{
    public const string ItemName = "Late Fee";
    public const string IncomeAccountName = "Late Fee Income";

    /// <summary>The fees due on <paramref name="today"/>; none while late fees are off.</summary>
    public async Task<IReadOnlyList<LateFeeDue>> DueAsync(DateOnly today, CancellationToken ct = default)
    {
        var settings = await collections.GetAsync(ct);
        if (!settings.LateFeesEnabled)
            return [];
        var all = await store.ListInvoicesAsync(null, ct);
        var lastFee = all.Where(i => i.LateFeeForInvoiceId is not null && i.Status == DocumentStatus.Posted)
            .GroupBy(i => i.LateFeeForInvoiceId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(i => i.Date));
        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var due = new List<LateFeeDue>();
        foreach (var invoice in all.Where(i => i.IsOpen && i.LateFeeForInvoiceId is null && i.Date <= today)
                     .OrderBy(i => i.DueDate).ThenBy(i => i.Number, StringComparer.Ordinal))
        {
            var customer = customers[invoice.CustomerId];
            var last = lastFee.TryGetValue(invoice.Id, out var d) ? d : (DateOnly?)null;
            if (customer.NoLateFees || !customer.IsActive || !settings.FeeDue(invoice.DueDate, last, today))
                continue;
            var fee = settings.FeeFor(invoice.Balance);
            if (fee > 0m)
                due.Add(new LateFeeDue(invoice, customer, today.DayNumber - invoice.DueDate.DayNumber, last, fee));
        }

        return due;
    }

    /// <summary>Charges the fees due today (only those on <paramref name="invoiceIds"/> when given), each its own invoice and save.</summary>
    public async Task<IReadOnlyList<Invoice>> ChargeAsync(DateOnly today, IReadOnlyCollection<Guid>? invoiceIds = null, CancellationToken ct = default)
    {
        var due = (await DueAsync(today, ct)).Where(d => invoiceIds is null || invoiceIds.Contains(d.Invoice.Id)).ToList();
        if (due.Count == 0)
            return [];
        var itemId = await ItemAsync(ct);
        var created = new List<Invoice>();
        foreach (var fee in due)
        {
            var description = $"Late fee: invoice {fee.Invoice.Number}, {fee.Invoice.Balance:N2} overdue since {fee.Invoice.DueDate:d}";
            created.Add(await invoices.CreateAsync(
                new CreateInvoiceCommand(fee.Customer.Id, today, [new SalesLineDraft(itemId, description, 1m, fee.Fee, Taxable: false)],
                    DueDate: today, Memo: $"Late fee on invoice {fee.Invoice.Number}", TaxCodeId: Guid.Empty),
                recurringInvoiceId: null, ct, lateFeeFor: fee.Invoice.Id));
        }

        return created;
    }

    /// <summary>The settings' fee item while it's active, else "Late Fee" (found or created, with "Late Fee Income"), remembered.</summary>
    private async Task<Guid> ItemAsync(CancellationToken ct)
    {
        var settings = await collections.EnsureAsync(ct);
        if (settings.LateFeeItemId is { } saved && await items.GetAsync(saved, ct) is { IsActive: true })
            return saved;

        var item = (await items.ListAsync(includeInactive: false, ct)).FirstOrDefault(i => string.Equals(i.Name, ItemName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            var chart = await accounts.GetChartAsync(ct);
            var income = chart.Ordered(includeInactive: false).Select(x => x.Account)
                .FirstOrDefault(a => a.Type.ClassOf() == AccountClass.Income && string.Equals(a.Name, IncomeAccountName, StringComparison.OrdinalIgnoreCase))
                ?? await accounts.CreateAsync(new CreateAccountCommand(IncomeAccountName, AccountType.OtherIncome,
                    Description: "Late fees charged on overdue invoices."), ct);
            item = await items.CreateAsync(new ItemDetails(ItemName, Domain.Sales.ItemType.Service, income.Id, 0m, "Late fee on an overdue invoice", Taxable: false), ct);
        }

        var before = CollectionService.Snapshot(settings);
        settings.SetLateFeeItem(item.Id);
        var company = await companies.GetRequiredCompanyAsync(ct);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, CollectionService.EntityType, company.Id, before, CollectionService.Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        return item.Id;
    }
}

/// <summary>A payment reminder due today: the schedule step reached and where it goes.</summary>
public sealed record ReminderDue(Invoice Invoice, Customer Customer, int Step, string To);

/// <param name="Error">Why it wasn't sent.</param>
public sealed record ReminderResult(Invoice Invoice, int Step, string? Error);

/// <summary>
/// Payment reminders: for each open invoice, the latest step of the schedule reached (days from the due date) is
/// emailed once with the invoice attached, skipping steps already passed by a later one. Customers marked "no
/// reminders" or without an email address are skipped.
/// </summary>
public sealed class ReminderService(ISalesStore store, ICompanyStore companies, CollectionService collections, EmailService email, IClock clock)
{
    /// <summary>Reminders due on <paramref name="today"/> (whether or not sending is switched on).</summary>
    public async Task<IReadOnlyList<ReminderDue>> DueAsync(DateOnly today, CancellationToken ct = default)
    {
        var settings = await collections.GetAsync(ct);
        if (settings.ReminderSteps.Count == 0)
            return [];
        var sent = (await store.ListRemindersAsync(ct)).GroupBy(r => r.InvoiceId).ToDictionary(g => g.Key, g => g.Max(r => r.Step));
        var customers = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var due = new List<ReminderDue>();
        foreach (var invoice in (await store.ListOpenInvoicesAsync(null, ct)).OrderBy(i => i.DueDate).ThenBy(i => i.Number, StringComparer.Ordinal))
        {
            // A sub-customer billed with its parent is chased through the parent.
            var customer = customers[CustomerTree.BillTo(invoice.CustomerId, customers)];
            if (customer.NoReminders || customer.Email is null)
                continue;
            var step = settings.ReminderDue(invoice.Date, invoice.DueDate, sent.TryGetValue(invoice.Id, out var last) ? last : null, today);
            if (step is { } s)
                due.Add(new ReminderDue(invoice, customer, s, customer.Email));
        }

        return due;
    }

    /// <summary>
    /// Emails the reminders due today (only for <paramref name="invoiceIds"/> when given). Each send is recorded on
    /// its own; a failure is reported and the rest still go.
    /// </summary>
    public async Task<IReadOnlyList<ReminderResult>> SendAsync(DateOnly today, IReadOnlyCollection<Guid>? invoiceIds = null, CancellationToken ct = default)
    {
        var due = (await DueAsync(today, ct)).Where(d => invoiceIds is null || invoiceIds.Contains(d.Invoice.Id)).ToList();
        if (due.Count == 0)
            return [];
        var settings = await collections.GetAsync(ct);
        var company = (await companies.GetRequiredCompanyAsync(ct)).Name;
        var results = new List<ReminderResult>();
        foreach (var reminder in due)
        {
            var i = reminder.Invoice;
            string Fill(string template) => CollectionSettings.Fill(template, reminder.Customer.DisplayName, i.Number, i.Balance, i.DueDate, today, company);
            try
            {
                await email.SendAsync(new EmailSource(Domain.Email.EmailKind.Reminder, i.Id),
                    new EmailDraft(reminder.To, Fill(settings.ReminderSubject), Fill(settings.ReminderBody), reminder.Customer.EmailCc), ct);
                store.Add(InvoiceReminder.Create(i.Id, reminder.Step, clock.UtcNow, reminder.To));
                await store.SaveChangesAsync(ct);
                results.Add(new ReminderResult(i, reminder.Step, null));
            }
            catch (DomainException ex)
            {
                results.Add(new ReminderResult(i, reminder.Step, ex.Message));
                if (ex.Code is "email.not_configured" or "email.password_unreadable")
                    break;
            }
        }

        return results;
    }

    /// <summary>The reminders sent for an invoice, oldest first.</summary>
    public async Task<IReadOnlyList<InvoiceReminder>> ForInvoiceAsync(Guid invoiceId, CancellationToken ct = default) =>
        (await store.ListRemindersAsync(ct)).Where(r => r.InvoiceId == invoiceId).OrderBy(r => r.SentAt).ToList();
}
