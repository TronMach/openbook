namespace OpenBook.Api;

/// <summary>
/// A correction through the API sends what it knows of; what it leaves out stays as it was, or a program that reads a
/// document and sends it back would clear what the response doesn't carry. <see cref="Guid.Empty"/> clears.
/// </summary>
internal static class ApiKeep
{
    public static Guid? Id(Guid? sent, Guid? current) => sent == Guid.Empty ? null : sent ?? current;
}
