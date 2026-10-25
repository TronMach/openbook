using OpenBook.Domain.Common;

namespace OpenBook.Domain.Segments;

public enum SegmentKind
{
    /// <summary>Set per line: a product line, fund, service type... Reports can run by class.</summary>
    Class,

    /// <summary>Set per transaction: a store, region, department or property (the company names it).</summary>
    Location,

    /// <summary>Free labels on transactions, any number each; for filtering, not for statements.</summary>
    Tag,
}

/// <summary>
/// A way to slice the books without more accounts: classes, locations (departments) and tags. Classes and
/// locations can have subsegments (full names "Parent:Child", at most <see cref="MaxDepth"/> levels); tags are flat.
/// Deactivated, never deleted, so history keeps its names.
/// </summary>
public sealed class Segment
{
    public const int MaxDepth = 5;

    private Segment() => Name = string.Empty;

    public Guid Id { get; private set; }
    public SegmentKind Kind { get; private set; }
    public string Name { get; private set; }
    public Guid? ParentId { get; private set; }
    public bool IsActive { get; private set; }

    public static Segment Create(SegmentKind kind, string name, Guid? parentId = null)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("segment.kind_invalid", $"Unknown segment kind {kind}.");
        var segment = new Segment { Id = Guid.CreateVersion7(), Kind = kind, IsActive = true };
        segment.Rename(name);
        segment.Move(parentId);
        return segment;
    }

    public void Rename(string name) =>
        Name = Text.Optional(name, 100, "segment.name_too_long", "Name") is { } n
            ? n.Contains(':', StringComparison.Ordinal)
                ? throw new DomainException("segment.name_colon", "Names can't contain ':' (it separates parent and child).")
                : n
            : throw new DomainException("segment.name_required", "Enter a name.");

    /// <summary>Hierarchy rules (same kind, no cycles, depth) are checked by <see cref="SegmentRules.Validate"/>, which sees every segment.</summary>
    public void Move(Guid? parentId)
    {
        if (parentId is not null && Kind == SegmentKind.Tag)
            throw new DomainException("segment.tag_parent", "Tags can't have a parent.");
        ParentId = parentId == Guid.Empty ? null : parentId;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

/// <summary>
/// The class and location of one posted journal line. Kept beside the ledger rather than in it, so posted lines
/// stay immutable while segments can still be corrected (reclassified). Only lines with a segment have a row.
/// </summary>
public sealed class LineSegment
{
    private LineSegment() { }

    public LineSegment(Guid journalLineId, Guid journalEntryId, Guid? classId, Guid? locationId)
    {
        JournalLineId = journalLineId;
        JournalEntryId = journalEntryId;
        Set(classId, locationId);
    }

    public Guid JournalLineId { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public Guid? ClassId { get; private set; }
    public Guid? LocationId { get; private set; }

    public bool IsEmpty => ClassId is null && LocationId is null;

    public void Set(Guid? classId, Guid? locationId)
    {
        ClassId = classId == Guid.Empty ? null : classId;
        LocationId = locationId == Guid.Empty ? null : locationId;
    }
}

/// <summary>A tag on a journal entry (and so on the document that posted it).</summary>
public sealed class EntryTag
{
    private EntryTag() { }

    public EntryTag(Guid journalEntryId, Guid tagId) => (JournalEntryId, TagId) = (journalEntryId, tagId);

    public Guid JournalEntryId { get; private set; }
    public Guid TagId { get; private set; }
}

/// <summary>Rules across segments and the names reports show.</summary>
public static class SegmentRules
{
    public const string NotSpecified = "Not specified";

    /// <summary>Same kind, no cycles, at most <see cref="Segment.MaxDepth"/> levels, unique names among siblings (ignoring case).</summary>
    public static void Validate(Segment segment, IReadOnlyCollection<Segment> all)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(all);
        var byId = all.Where(s => s.Id != segment.Id).Append(segment).ToDictionary(s => s.Id);
        if (segment.ParentId is { } parentId)
        {
            var parent = byId.GetValueOrDefault(parentId) ?? throw new EntityNotFoundException("segment", parentId);
            if (parent.Kind != segment.Kind)
                throw new DomainException("segment.parent_kind", "A subsegment's parent must be the same kind.");
            var depth = 1;
            for (var at = parent; at is not null; at = at.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
            {
                if (at.Id == segment.Id)
                    throw new DomainException("segment.cycle", "A segment can't be under itself.");
                if (++depth > Segment.MaxDepth)
                    throw new DomainException("segment.too_deep", $"Segments can be at most {Segment.MaxDepth} levels deep.");
            }

            // Children of this segment add their own depth below it.
            if (depth + Height(segment.Id, byId.Values) > Segment.MaxDepth)
                throw new DomainException("segment.too_deep", $"Segments can be at most {Segment.MaxDepth} levels deep.");
        }

        if (byId.Values.Any(s => s.Id != segment.Id && s.Kind == segment.Kind && s.ParentId == segment.ParentId
                && string.Equals(s.Name, segment.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("segment.name_taken", $"There's already one named '{segment.Name}' there.");
    }

    private static int Height(Guid id, IEnumerable<Segment> all)
    {
        var children = all.Where(s => s.ParentId == id).ToList();
        return children.Count == 0 ? 0 : 1 + children.Max(c => Height(c.Id, all));
    }

    /// <summary>"Parent:Child" names for every segment.</summary>
    public static IReadOnlyDictionary<Guid, string> FullNames(IReadOnlyCollection<Segment> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        var byId = all.ToDictionary(s => s.Id);
        var names = new Dictionary<Guid, string>();
        string Name(Segment s, int guard) =>
            names.TryGetValue(s.Id, out var known) ? known
            : names[s.Id] = s.ParentId is { } p && byId.TryGetValue(p, out var parent) && guard < Segment.MaxDepth
                ? $"{Name(parent, guard + 1)}:{s.Name}"
                : s.Name;
        foreach (var s in all)
            Name(s, 0);
        return names;
    }

    /// <summary>The segment and everything under it.</summary>
    public static IReadOnlySet<Guid> WithDescendants(Guid id, IReadOnlyCollection<Segment> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        var result = new HashSet<Guid> { id };
        for (var added = true; added;)
        {
            added = false;
            foreach (var s in all.Where(s => s.ParentId is { } p && result.Contains(p) && !result.Contains(s.Id)))
                added = result.Add(s.Id) || added;
        }

        return result;
    }
}
