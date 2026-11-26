using OpenBook.Application.Documents;
using OpenBook.Domain.Documents;

namespace OpenBook.Integration.Tests;

/// <summary>Stands in for Claude reading documents: answers with <see cref="Answer"/>, or fails with <see cref="Failure"/>, and records each call.</summary>
public sealed class FakeClaude : IClaudeDocumentReader, OpenBook.Application.Settlements.IClaudeSettlementReader
{
    public OpenBook.Application.Settlements.SettlementReading? Settlement { get; set; }

    /// <summary>The files put to it as settlements.</summary>
    public List<byte[]> Statements { get; } = [];

    public Task<OpenBook.Application.Settlements.SettlementReading> ReadAsync(string apiKey, string model, string fileName, byte[] content, DateOnly reference, CancellationToken ct)
    {
        Statements.Add(content);
        return Failure is { } message ? throw new DocumentReadException(message) : Task.FromResult(Settlement!);
    }

    public DocumentFields Answer { get; set; } = DocumentFields.Empty;
    public string? Failure { get; set; }
    public List<(string ApiKey, string Model, DocumentKind Kind, string FileName)> Calls { get; } = [];

    public bool CanRead(string fileName) => !fileName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentFields> ReadAsync(string apiKey, string model, DocumentKind kind, string fileName, byte[] content, CancellationToken ct)
    {
        Calls.Add((apiKey, model, kind, fileName));
        return Failure is { } message ? throw new DocumentReadException(message) : Task.FromResult(Answer);
    }
}
