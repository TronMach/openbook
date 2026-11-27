using System.Globalization;
using System.Security.Cryptography;
using OpenBook.Application.Attachments;
using OpenBook.Application.Documents;
using OpenBook.Application.Forms;
using OpenBook.Domain.Attachments;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

/// <summary>A word on a page: where it is (in the page's own units, from the top left) and how sure its reading is, 0 to 100.</summary>
public sealed record StatementWord(int Page, string Text, double Confidence, double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Middle => Top + Height / 2;
}

/// <param name="Start">The first words of its text, to tell pages apart; null for a picture of a page.</param>
/// <param name="Preview">A small picture of it (PNG), when it's a picture.</param>
public sealed record StatementPage(int Number, string? Start, byte[]? Preview);

/// <param name="FromPicture">Whether the words came from a picture, which is read by OCR and often wrong.</param>
public sealed record StatementWords(bool FromPicture, IReadOnlyList<StatementWord> Words);

/// <summary>The pages of a statement file. Nothing here leaves the server. Implemented in Infrastructure.</summary>
public interface IStatementPages
{
    /// <summary>Whether pictures can be read here (an OCR program is set up).</summary>
    bool CanReadPictures { get; }

    /// <exception cref="DomainException">The file can't be opened.</exception>
    IReadOnlyList<StatementPage> Inspect(string fileName, byte[] content);

    /// <summary>A file of these pages only.</summary>
    /// <exception cref="DomainException">The file can't be split, so none of it may be sent or kept.</exception>
    byte[] Extract(string fileName, byte[] content, IReadOnlyCollection<int> pages);

    /// <summary>Every word on these pages; null when they're pictures and can't be read here.</summary>
    Task<StatementWords?> WordsAsync(string fileName, byte[] content, IReadOnlyCollection<int> pages, CancellationToken ct);
}

/// <summary>Reads a settlement statement with Claude (Anthropic's API). Implemented in Infrastructure.</summary>
public interface IClaudeSettlementReader
{
    /// <exception cref="DocumentReadException">Claude couldn't be reached, refused, or gave nothing usable.</exception>
    Task<SettlementReading> ReadAsync(string apiKey, string model, string fileName, byte[] content, DateOnly reference, CancellationToken ct);
}

public enum SettlementReadWith
{
    /// <summary>Sent to Claude: only once an admin has switched document reading with Claude on.</summary>
    Claude,

    /// <summary>On this server: the text in a PDF exactly, or a picture by OCR, which is a backup and often wrong.</summary>
    ThisComputer,
}

/// <summary>
/// Settlements read from a statement's pages. A person chooses the pages first: only those are sent, read and kept, since
/// statements often carry other people's pay. Reading never posts: the settlement waits to be checked, and one read by OCR
/// can't be posted until every line has been checked and the net paid typed by a person.
/// </summary>
public sealed class SettlementCaptureService(
    ISettlementStore store, IStatementPages pages, IClaudeSettlementReader claude, DocumentCaptureService documents, ISecretProtector secrets, AttachmentService attachments,
    IUnitOfWork unitOfWork, IClock clock, ICurrentUser user)
{
    public const int MaxPages = 100;

    private static readonly string[] Types = [".pdf", ".png", ".jpg", ".jpeg", ".webp", ".gif"];

    /// <summary>Whether statements can be read with Claude (an admin has switched it on).</summary>
    public async Task<bool> CanUseClaudeAsync(CancellationToken ct = default) => (await documents.GetSettingsAsync(ct)).IsReady;

    /// <summary>Whether pictures of pages can be read on this server.</summary>
    public bool CanReadPictures => pages.CanReadPictures;

    public IReadOnlyList<StatementPage> Inspect(string fileName, byte[] content)
    {
        Check(fileName, content);
        var found = pages.Inspect(fileName, content);
        return found.Count <= MaxPages ? found
            : throw new DomainException("settlement.too_many_pages", $"The file has {found.Count} pages; at most {MaxPages} can be looked at. Save the settlement's pages on their own.");
    }

    /// <param name="date">The pay date, when the statement doesn't give one.</param>
    public async Task<Settlement> ReadAsync(
        Guid carrierId, string fileName, byte[] content, IReadOnlyCollection<int> chosen, SettlementReadWith with, DateOnly date, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        Check(fileName, content);
        var carrier = await store.GetCarrierAsync(carrierId, ct) ?? throw new EntityNotFoundException(SettlementService.CarrierEntity, carrierId);
        if (!carrier.IsActive)
            throw new DomainException("settlement.carrier_inactive", $"{carrier.Name} is inactive.");
        var wanted = chosen.Distinct().Order().ToList();
        var total = pages.Inspect(fileName, content).Count;
        if (wanted.Count == 0)
            throw new DomainException("settlement.no_pages", "Choose the pages to read.");
        if (wanted[0] < 1 || wanted[^1] > total)
            throw new DomainException("settlement.page_missing", $"The file has {total} page{(total == 1 ? "" : "s")}: choose from those.");

        // Only the chosen pages go anywhere from here on.
        var kept = pages.Extract(fileName, content, wanted);
        var keptPages = Enumerable.Range(1, wanted.Count).ToList();
        SettlementReading reading;
        string method;
        if (with == SettlementReadWith.Claude)
        {
            var settings = await documents.GetSettingsAsync(ct);
            if (!settings.IsReady)
                throw new DomainException("settlement.claude_off", "Reading with Claude isn't switched on. An admin can under Settings → Document reading.");
            var key = secrets.Unprotect(settings.ProtectedApiKey!)
                ?? throw new DomainException("settlement.claude_key", "The Anthropic API key can't be read on this server; an admin can enter it again in Settings.");
            try
            {
                reading = await claude.ReadAsync(key, settings.Model, fileName, kept, date, ct);
            }
            catch (DocumentReadException ex)
            {
                throw new DomainException("settlement.read_failed", $"Claude couldn't read it: {ex.Message}. Nothing was entered.");
            }

            method = SettlementReadMethods.Claude;
        }
        else
        {
            var words = await pages.WordsAsync(fileName, kept, keptPages, ct)
                ?? throw new DomainException("settlement.cannot_read_here", "These pages are pictures, and no OCR program is set up on this server to read them. Import a CSV or type the lines instead.");
            reading = SettlementTextParser.Parse(words, date);
            method = words.FromPicture ? SettlementReadMethods.Ocr : SettlementReadMethods.Text;
        }

        return await SaveAsync(carrier, fileName, content, kept, wanted, reading, method, date, ct);
    }

    private async Task<Settlement> SaveAsync(
        Carrier carrier, string fileName, byte[] content, byte[] kept, List<int> wanted, SettlementReading reading, string method, DateOnly date, CancellationToken ct)
    {
        var notes = new List<string>();
        var lines = reading.Lines.Where(Usable).Select(Tidy).ToList();
        if (lines.Count < reading.Lines.Count)
            notes.Add($"{reading.Lines.Count - lines.Count} line(s) had no code, description or amount OpenBook can hold and were left out.");
        if (lines.Count == 0)
            throw new DomainException("settlement.file_no_lines", "No settlement lines could be read on those pages.");

        var held = (await store.ListSettlementsAsync(carrier.Id, ct)).Where(s => s.Status != SettlementStatus.Dismissed).ToList();
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (held.FirstOrDefault(s => s.FileHash == hash) is { } same)
            notes.Add($"This file was read before, as settlement {same.Number}.");

        var paid = reading.Date ?? date;
        var wantedNumber = SettlementNumber(reading.Number) ?? $"{paid:yyyy-MM-dd}";
        var number = wantedNumber;
        for (var n = 2; held.Any(s => s.Number == number); n++)
            number = $"{wantedNumber} ({n})";
        if (number != wantedNumber)
            notes.Add($"There's already a settlement {wantedNumber} from {carrier.Name}; check that this isn't the same one.");

        // A scan's net isn't taken: a person types it from the statement, so it's a check the scan can't have got wrong too.
        var scanned = method == SettlementReadMethods.Ocr;
        var (net, carried) = scanned ? (0m, 0m) : (reading.NetPaid ?? 0m, reading.CarriedForward ?? 0m);
        if (net < 0m)
            (net, carried) = (0m, carried + net);
        if (!scanned && reading.NetPaid is null)
            notes.Add("No net paid could be read: enter it from the statement.");

        var settlement = Settlement.Create(carrier.Id, new SettlementDetails(number, paid, Cents(net), lines, Valid(reading).From, Valid(reading).To, Cents(carried),
            StatedTotals: [.. (reading.StatedTotals ?? []).Where(t => Holds(t.Amount)).DistinctBy(t => t.Section)]), clock.UtcNow, user.Actor);
        settlement.SetFileHash(hash);
        notes.AddRange(reading.Notes ?? []);
        settlement.SetReading(method, string.Join(" ", notes));

        await using var transaction = await unitOfWork.BeginAsync(ct);
        store.Add(settlement);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, SettlementService.SettlementEntity, settlement.Id, null, SettlementService.Snapshot(settlement, carrier)));
        await store.SaveChangesAsync(ct);
        using var file = new MemoryStream(kept);
        var extension = Path.GetExtension(fileName);
        await attachments.AddAsync(SettlementService.SettlementEntity, settlement.Id,
            wanted.Count == 1 && extension != ".pdf" ? $"statement{extension}" : $"statement pages {PageList(wanted)}{extension}", file, ct);
        await transaction.CommitAsync(ct);
        return settlement;
    }

    /// <summary>"1-4, 7": pages as people write them.</summary>
    public static string PageList(IReadOnlyList<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var parts = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            var start = pages[i];
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1)
                i++;
            parts.Add(pages[i] == start ? $"{start}" : $"{start}-{pages[i]}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The pages "1-4, 7" names; refuses anything else.</summary>
    public static IReadOnlyList<int> ParsePages(string? text)
    {
        var pages = new SortedSet<int>();
        foreach (var part in (text ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-');
            if (ends.Length > 2 || !ends.All(e => int.TryParse(e, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                throw new DomainException("settlement.pages_unreadable", "Write the pages as numbers, like 1-4, 7.");
            var (first, last) = (int.Parse(ends[0], CultureInfo.InvariantCulture), int.Parse(ends[^1], CultureInfo.InvariantCulture));
            if (first < 1 || last < first || last > MaxPages)
                throw new DomainException("settlement.pages_unreadable", $"Pages go from 1 to {MaxPages}, like 1-4, 7.");
            pages.UnionWith(Enumerable.Range(first, last - first + 1));
        }

        return [.. pages];
    }

    private static void Check(string fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Types.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            throw new DomainException("settlement.file_type", "Choose a PDF, or a picture (PNG, JPEG, GIF or WebP).");
        if (content.Length == 0)
            throw new DomainException("settlement.file_empty", "The file is empty.");
        if (content.Length > Attachment.MaxBytes)
            throw new DomainException("settlement.file_too_large", $"Files can be at most {Attachment.MaxBytes / (1024 * 1024)} MB.");
    }

    private static (DateOnly? From, DateOnly? To) Valid(SettlementReading r) => r.PeriodStart is { } a && r.PeriodEnd is { } b && b < a ? (null, null) : (r.PeriodStart, r.PeriodEnd);

    private static string? SettlementNumber(string? read) => read?.Trim() is { Length: > 0 } n ? n.Length > 50 ? n[..50].TrimEnd() : n : null;

    private static bool Holds(decimal amount) => Math.Abs(amount) <= Money.MaxAmount && decimal.Round(amount, 2) == amount;

    private static decimal Cents(decimal amount) => Holds(amount) ? amount : 0m;

    private static bool Usable(SettlementLineDraft l) => Holds(l.Amount) && (!string.IsNullOrWhiteSpace(l.Code) || !string.IsNullOrWhiteSpace(l.Description));

    private static SettlementLineDraft Tidy(SettlementLineDraft l)
    {
        static decimal? Within(decimal? q) => q is >= 0m and <= 1_000_000m ? q : null;
        return l with { LoadedMiles = Within(l.LoadedMiles), EmptyMiles = Within(l.EmptyMiles), Gallons = Within(l.Gallons) };
    }
}
