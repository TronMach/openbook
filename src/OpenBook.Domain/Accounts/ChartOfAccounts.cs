using OpenBook.Domain.Common;

namespace OpenBook.Domain.Accounts;

/// <summary>
/// A snapshot of the whole chart, for rules that span accounts: a subaccount has its parent's type,
/// nesting is at most <see cref="MaxDepth"/> levels, sibling names and account numbers are unique.
/// </summary>
public sealed class ChartOfAccounts
{
    public const int MaxDepth = 5;

    private readonly Dictionary<Guid, Account> _byId;
    private readonly ILookup<Guid?, Account> _children;

    public ChartOfAccounts(IEnumerable<Account> accounts)
    {
        _byId = accounts.ToDictionary(a => a.Id);
        _children = _byId.Values.ToLookup(a => a.ParentId);
    }

    public IReadOnlyCollection<Account> All => _byId.Values;

    public Account? Find(Guid id) => _byId.GetValueOrDefault(id);

    public Account Get(Guid id) => Find(id) ?? throw new EntityNotFoundException("account", id);

    public IEnumerable<Account> ChildrenOf(Guid? parentId) => _children[parentId];

    /// <summary>"Parent:Child:Grandchild", QuickBooks' full-name convention.</summary>
    public string FullName(Guid id) => string.Join(Account.PathSeparator, Ancestry(Get(id)).Reverse().Select(a => a.Name));

    /// <summary>1 for a top-level account.</summary>
    public int Depth(Guid id) => Ancestry(Get(id)).Count();

    public Account? FindByFullName(string fullName)
    {
        var parts = fullName.Split(Account.PathSeparator).Select(p => p.Trim()).ToArray();
        Account? current = null;
        foreach (var part in parts)
        {
            current = ChildrenOf(current?.Id).FirstOrDefault(a => string.Equals(a.Name, part, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                return null;
        }

        return current;
    }

    /// <summary>Depth-first, siblings ordered by type, then number, then name: the order a chart is read in.</summary>
    public IEnumerable<(Account Account, int Depth)> Ordered(bool includeInactive = true)
    {
        IEnumerable<(Account, int)> Walk(Guid? parentId, int depth)
        {
            foreach (var account in Sort(ChildrenOf(parentId)))
            {
                if (!includeInactive && !account.IsActive)
                    continue;
                yield return (account, depth);
                foreach (var child in Walk(account.Id, depth + 1))
                    yield return child;
            }
        }

        return Walk(null, 1);
    }

    public void EnsureCanAdd(string name, AccountType type, string? number, Guid? parentId)
    {
        if (parentId is { } pid)
        {
            var parent = Find(pid) ?? throw new EntityNotFoundException("account", pid);
            if (!parent.IsActive)
                throw new DomainException("account.parent_inactive", $"'{parent.Name}' is inactive, so it can't have new subaccounts.");
            if (parent.Type != type)
                throw new DomainException("account.parent_type", $"A subaccount must have its parent's type ({parent.Type.Describe().ToLowerInvariant()}).");
            if (Depth(pid) + 1 > MaxDepth)
                throw new DomainException("account.too_deep", $"Accounts can be nested at most {MaxDepth} levels deep.");
        }

        EnsureUniqueName(name, parentId, except: null);
        EnsureUniqueNumber(number, except: null);
    }

    /// <param name="hasPostings">Whether the account has ledger lines; limits type changes.</param>
    public void EnsureCanUpdate(Account account, string name, string? number, AccountType type, Guid? parentId, bool hasPostings)
    {
        ArgumentNullException.ThrowIfNull(account);
        var children = ChildrenOf(account.Id).ToList();

        if (type != account.Type)
        {
            if (children.Count > 0)
                throw new DomainException("account.type_has_children", "Change the type of the subaccounts first, or move them elsewhere.");
            if (hasPostings && type.ClassOf() != account.Type.ClassOf())
                throw new DomainException("account.type_class_change",
                    $"This account has transactions, so it can only change to another {account.Type.ClassOf().ToString().ToLowerInvariant()} type.");
        }

        if (parentId is { } pid)
        {
            var parent = Find(pid) ?? throw new EntityNotFoundException("account", pid);
            if (pid == account.Id || Ancestry(parent).Any(a => a.Id == account.Id))
                throw new DomainException("account.parent_cycle", "An account can't be moved under itself or one of its subaccounts.");
            if (parent.Type != type)
                throw new DomainException("account.parent_type", $"A subaccount must have its parent's type ({parent.Type.Describe().ToLowerInvariant()}).");
            if (!parent.IsActive && parentId != account.ParentId)
                throw new DomainException("account.parent_inactive", $"'{parent.Name}' is inactive, so it can't have new subaccounts.");
            if (Depth(pid) + Height(account) > MaxDepth)
                throw new DomainException("account.too_deep", $"Accounts can be nested at most {MaxDepth} levels deep.");
        }

        EnsureUniqueName(name, parentId, except: account.Id);
        EnsureUniqueNumber(number, except: account.Id);
    }

    public void EnsureCanDeactivate(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (ChildrenOf(account.Id).Any(c => c.IsActive))
            throw new DomainException("account.active_children", "Make its subaccounts inactive first.");
    }

    public void EnsureCanReactivate(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.ParentId is { } pid && Find(pid) is { IsActive: false } parent)
            throw new DomainException("account.parent_inactive", $"Make '{parent.Name}' active first.");
    }

    private void EnsureUniqueName(string name, Guid? parentId, Guid? except)
    {
        var normalized = Account.NormalizeName(name);
        if (ChildrenOf(parentId).Any(a => a.Id != except && string.Equals(a.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("account.name_taken",
                parentId is null
                    ? $"There's already a top-level account named '{normalized}'."
                    : $"'{FullName(parentId.Value)}' already has a subaccount named '{normalized}'.");
    }

    private void EnsureUniqueNumber(string? number, Guid? except)
    {
        var normalized = Account.NormalizeNumber(number);
        if (normalized is not null && _byId.Values.FirstOrDefault(a => a.Id != except && a.Number == normalized) is { } other)
            throw new DomainException("account.number_taken", $"Account number {normalized} is already used by '{FullName(other.Id)}'.");
    }

    private IEnumerable<Account> Ancestry(Account account)
    {
        var seen = new HashSet<Guid>();
        for (var current = account; current is not null && seen.Add(current.Id); current = current.ParentId is { } p ? Find(p) : null)
            yield return current;
    }

    /// <summary>Levels in the subtree rooted at <paramref name="account"/>, itself included.</summary>
    private int Height(Account account) => 1 + ChildrenOf(account.Id).Select(Height).DefaultIfEmpty(0).Max();

    private static IEnumerable<Account> Sort(IEnumerable<Account> accounts) => accounts
        .OrderBy(a => a.Type)
        .ThenBy(a => a.Number is null)
        .ThenBy(a => a.Number, StringComparer.Ordinal)
        .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
}
