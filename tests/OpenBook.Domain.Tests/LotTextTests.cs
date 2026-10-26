using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;

namespace OpenBook.Domain.Tests;

public sealed class LotTextTests
{
    [Fact]
    public void Lots_take_quantities_or_the_whole_line_and_must_add_up()
    {
        Assert.Equal([("A-1", 3m), ("B-2", 2m)], LotText.Parse("A-1 x 3; B-2 x 2", ItemTracking.Lot, 5m, "Flour"));
        Assert.Equal([("A-1", 5m)], LotText.Parse(" A-1 ", ItemTracking.Lot, 5m, "Flour"));
        Assert.Empty(LotText.Parse("  ", ItemTracking.Lot, 5m, "Flour"));
        Assert.Empty(LotText.Parse("A-1", ItemTracking.None, 5m, "Flour"));
        Assert.Equal("lot.quantity_mismatch", Assert.Throws<DomainException>(() => LotText.Parse("A x 1", ItemTracking.Lot, 2m, "Flour")).Code);
        Assert.Equal("lot.quantity_required", Assert.Throws<DomainException>(() => LotText.Parse("A; B", ItemTracking.Lot, 2m, "Flour")).Code);
        Assert.Equal("lot.duplicate", Assert.Throws<DomainException>(() => LotText.Parse("A x 1; a x 1", ItemTracking.Lot, 2m, "Flour")).Code);
        Assert.Equal("A-1 x 3; B-2 x 2", LotText.Format([("A-1", 1m), ("B-2", 2m), ("a-1", 2m)], ItemTracking.Lot));
    }

    [Fact]
    public void Serials_are_one_per_whole_unit()
    {
        Assert.Equal([("S1", 1m), ("S2", 1m)], LotText.Parse("S1, S2", ItemTracking.Serial, 2m, "Phone"));
        Assert.Equal([("S1", 1m), ("S2", 1m)], LotText.Parse("S1\nS2", ItemTracking.Serial, 2m, "Phone"));
        Assert.Equal("lot.serial_count", Assert.Throws<DomainException>(() => LotText.Parse("S1", ItemTracking.Serial, 2m, "Phone")).Code);
        Assert.Equal("lot.serial_whole", Assert.Throws<DomainException>(() => LotText.Parse("S1", ItemTracking.Serial, 1.5m, "Phone")).Code);
        Assert.Equal("S1, S2", LotText.Format([("S1", 1m), ("S2", 1m)], ItemTracking.Serial));
    }
}
