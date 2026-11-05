namespace OpenBook.Domain.Common;

/// <summary>A business rule was violated. <see cref="Code"/> is stable and safe to show to API clients.</summary>
public class DomainException : Exception
{
    public DomainException(string code, string message) : base(message) => Code = code;

    protected DomainException(string code, string message, Exception? inner) : base(message, inner) => Code = code;

    public string Code { get; }
}

public sealed class EntityNotFoundException(string entityType, Guid id)
    : DomainException($"{entityType}.not_found", $"{entityType} {id} was not found.");

/// <summary>
/// A save collided with someone else's at the same moment (the same record changed, or the same number taken), so
/// nothing was saved. Running the operation again from the start usually succeeds; the API answers 409.
/// </summary>
public sealed class ConcurrencyConflictException(Exception inner)
    : DomainException(ConflictCode, "Something else changed this at the same moment, perhaps a second click, so this save didn't go through. Check whether it's already there before trying again.", inner)
{
    public const string ConflictCode = "save.conflict";
}
