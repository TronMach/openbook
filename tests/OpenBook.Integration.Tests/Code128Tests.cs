using OpenBook.Application.Inventory;
using OpenBook.Domain.Common;

namespace OpenBook.Integration.Tests;

public sealed class Code128Tests
{
    [Fact]
    public void Every_symbol_is_eleven_modules_and_the_stop_thirteen()
    {
        Assert.Equal(107, Code128.Table.Count);
        Assert.All(Code128.Table.Take(106), p => Assert.Equal(11, p.Sum(c => c - '0')));
        Assert.Equal(13, Code128.Table[106].Sum(c => c - '0'));
        Assert.Equal(106, Code128.Table.Take(106).Distinct().Count());
    }

    [Fact]
    public void Text_uses_set_B_and_even_digit_runs_use_set_C_with_a_weighted_checksum()
    {
        // Start B (104), "A" (33), checksum (104 + 33) % 103 = 34.
        Assert.Equal([104, 33, 34], Code128.Values("A"));
        // Start C (105), 12, 34, checksum (105 + 12 + 34 * 2) % 103 = 82.
        Assert.Equal([105, 12, 34, 82], Code128.Values("1234"));
        // Odd-length digits stay in set B.
        Assert.Equal(104, Code128.Values("123")[0]);
        // Start bar to final stop bar: 11 modules per symbol, 13 for the stop.
        Assert.Equal(11 * 3 + 13, Code128.Modules("A").Sum());
        Assert.Equal("barcode.invalid", Assert.Throws<DomainException>(() => Code128.Values("tab\there")).Code);
    }
}
