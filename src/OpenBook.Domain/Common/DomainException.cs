namespace OpenBook.Domain.Common;

/// <summary>A business rule was violated. <see cref="Code"/> is stable and safe to show to API clients.</summary>
public class DomainException : Exception
{
    public DomainException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

public sealed class EntityNotFoundException(string entityType, Guid id)
    : DomainException($"{entityType}.not_found", $"{entityType} {id} was not found.");
