using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;

namespace OpenBook.Domain.Tests;

public sealed class SegmentTests
{
    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    [Fact]
    public void Full_names_and_descendants_follow_the_tree()
    {
        var retail = Segment.Create(SegmentKind.Class, "Retail");
        var stores = Segment.Create(SegmentKind.Class, "Stores", retail.Id);
        var north = Segment.Create(SegmentKind.Class, "North", stores.Id);
        var other = Segment.Create(SegmentKind.Class, "Other");
        Segment[] all = [retail, stores, north, other];

        Assert.Equal("Retail:Stores:North", SegmentRules.FullNames(all)[north.Id]);
        Assert.Equal(new HashSet<Guid> { retail.Id, stores.Id, north.Id }, SegmentRules.WithDescendants(retail.Id, all));
        Assert.Equal(new HashSet<Guid> { other.Id }, SegmentRules.WithDescendants(other.Id, all));
    }

    [Fact]
    public void Hierarchy_rules_are_enforced()
    {
        var a = Segment.Create(SegmentKind.Location, "A");
        var chain = new List<Segment> { a };
        for (var i = 1; i < Segment.MaxDepth; i++)
            chain.Add(Segment.Create(SegmentKind.Location, $"L{i}", chain[^1].Id));
        var tooDeep = Segment.Create(SegmentKind.Location, "Deep", chain[^1].Id);
        Assert.Equal("segment.too_deep", CodeOf(() => SegmentRules.Validate(tooDeep, chain)));

        var duplicate = Segment.Create(SegmentKind.Location, "a");
        Assert.Equal("segment.name_taken", CodeOf(() => SegmentRules.Validate(duplicate, chain)));
        // The same name is fine as a class, or under another parent.
        SegmentRules.Validate(Segment.Create(SegmentKind.Class, "A"), chain);
        SegmentRules.Validate(Segment.Create(SegmentKind.Location, "A", chain[1].Id), chain);

        Assert.Equal("segment.tag_parent", CodeOf(() => Segment.Create(SegmentKind.Tag, "Promo", a.Id)));
        Assert.Equal("segment.name_colon", CodeOf(() => Segment.Create(SegmentKind.Class, "A:B")));
    }

    [Fact]
    public void Sales_postings_split_income_by_class()
    {
        var income = Guid.NewGuid();
        var item = Item.Create(new ItemDetails("Widget", ItemType.Service, income, 10m));
        var retail = Guid.NewGuid();
        var lines = SalesLine.FromDrafts(
        [
            new SalesLineDraft(item.Id, null, 1m, 10m, ClassId: retail),
            new SalesLineDraft(item.Id, null, 2m, 10m),
            new SalesLineDraft(item.Id, null, 3m, 10m, ClassId: retail),
        ], new Dictionary<Guid, Item> { [item.Id] = item });

        var postings = SalesLine.Postings(Guid.NewGuid(), lines, null);
        Assert.Equal(3, postings.Count);
        Assert.Equal(40m, postings.Single(p => p.ClassId == retail).Credit);
        Assert.Equal(20m, postings.Single(p => p.AccountId == income && p.ClassId is null).Credit);
        Assert.Equal(retail, lines[0].ToDraft().ClassId);
    }
}
