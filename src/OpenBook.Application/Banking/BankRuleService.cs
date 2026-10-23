using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

/// <summary>Bank rules: how the bank feed suggests (or adds) categories for statement lines.</summary>
public sealed class BankRuleService(IBankingStore bank, IPurchasesStore purchases, AccountService accounts, IClock clock, ICurrentUser user)
{
    private const string EntityType = "bank_rule";

    /// <summary>In the order they're tried.</summary>
    public async Task<IReadOnlyList<BankRule>> ListAsync(CancellationToken ct = default) =>
        (await bank.ListRulesAsync(ct)).OrderBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<BankRule?> GetAsync(Guid id, CancellationToken ct = default) => bank.GetRuleAsync(id, ct);

    public async Task<BankRule> CreateAsync(BankRuleDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await CheckAsync(details, ct);
        var rule = BankRule.Create(details);
        bank.Add(rule);
        bank.Add(Audit(AuditAction.Created, rule, null));
        await bank.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<BankRule> UpdateAsync(Guid id, BankRuleDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rule = await bank.GetRuleAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await CheckAsync(details, ct);
        var before = Snapshot(rule);
        rule.Update(details);
        bank.Add(Audit(AuditAction.Updated, rule, before));
        await bank.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<BankRule> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var rule = await bank.GetRuleAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(rule);
        if (active)
            rule.Reactivate();
        else
            rule.Deactivate();
        bank.Add(Audit(AuditAction.Updated, rule, before));
        await bank.SaveChangesAsync(ct);
        return rule;
    }

    /// <summary>The category must be one a bank transaction can post to; the account a bank or card.</summary>
    private async Task CheckAsync(BankRuleDetails details, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var category = chart.Get(details.CategoryAccountId);
        if (!category.IsActive)
            throw new DomainException("rule.category_inactive", $"'{category.Name}' is inactive.");
        if (category.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable)
            throw new DomainException("rule.category_type", "Rules can't categorize to receivables or payables; record invoices and bills instead.");
        if (details.AccountId is { } accountId)
            BankingRules.EnsureBankingAccount(chart.Get(accountId));
        if (details.AccountId == details.CategoryAccountId)
            throw new DomainException("rule.category_self", "A rule can't categorize an account's lines to that same account.");
        if (details.VendorId is { } vendorId && await purchases.GetVendorAsync(vendorId, ct) is null)
            throw new EntityNotFoundException("vendor", vendorId);
    }

    private AuditRecord Audit(AuditAction action, BankRule r, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, Snapshot(r));

    private static string Snapshot(BankRule r) => JsonSerializer.Serialize(new
    {
        r.Name,
        r.Priority,
        r.AccountId,
        Direction = r.Direction.ToString(),
        r.Contains,
        r.MinAmount,
        r.MaxAmount,
        r.CategoryAccountId,
        r.Payee,
        r.VendorId,
        r.AutoAdd,
        r.IsActive,
    });
}
