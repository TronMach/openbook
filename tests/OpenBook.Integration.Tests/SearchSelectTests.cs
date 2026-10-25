using OpenBook.Web.Components.Search;

namespace OpenBook.Integration.Tests;

/// <summary>Search-as-you-type matching used by the pickers on transaction forms.</summary>
public sealed class SearchSelectTests
{
    private static readonly PickerOption[] Options =
    [
        new(Guid.NewGuid(), "Steel bolt", "M8, zinc plated"),
        new(Guid.NewGuid(), "Bolt cutter"),
        new(Guid.NewGuid(), "Widget", Keywords: "WID-100"),
        new(Guid.NewGuid(), "Café latte"),
        new(Guid.NewGuid(), "6100 · Office:Supplies", Group: "Expenses", Keywords: "6100"),
    ];

    private static string[] Find(string query) => PickerOptions.Filter(Options, query).Select(o => o.Label).ToArray();

    [Fact]
    public void Names_starting_with_the_text_come_first_then_words_then_anywhere()
    {
        Assert.Equal(["Bolt cutter", "Steel bolt"], Find("bolt"));
        Assert.Equal(["Bolt cutter", "Steel bolt"], Find("BOL"));
        Assert.Equal(["Widget"], Find("widg"));
    }

    [Fact]
    public void Every_word_must_match_somewhere_including_details_and_keywords()
    {
        Assert.Equal(["Steel bolt"], Find("bolt zinc"));
        Assert.Equal(["Widget"], Find("wid-100"));
        Assert.Equal(["6100 · Office:Supplies"], Find("suppl"));
        Assert.Equal(["6100 · Office:Supplies"], Find("6100"));
        Assert.Empty(Find("bolt widget"));
    }

    [Fact]
    public void Accents_and_case_are_ignored_and_empty_text_lists_everything()
    {
        Assert.Equal(["Café latte"], Find("cafe"));
        Assert.Equal(Options.Length, Find("  ").Length);
        Assert.Equal(2, PickerOptions.Filter(Options, "", max: 2).Count);
    }
}
