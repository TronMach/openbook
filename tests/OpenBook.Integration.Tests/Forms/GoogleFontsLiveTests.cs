using OpenBook.Domain.Common;
using OpenBook.Infrastructure.Forms;

namespace OpenBook.Integration.Tests.Forms;

/// <summary>Runs only with <c>OPENBOOK_TEST_LIVE_FONTS=1</c>: these call the real Google Fonts API.</summary>
public sealed class LiveFontsFactAttribute : FactAttribute
{
    public LiveFontsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENBOOK_TEST_LIVE_FONTS") != "1")
            Skip = "Set OPENBOOK_TEST_LIVE_FONTS=1 to call the real Google Fonts API.";
    }
}

/// <summary>
/// The real Google Fonts API, which the other font tests replace with <c>FakeGoogleFonts</c> (C559, checked
/// 2026-11-19): a family with no bold answers 200 with only its regular weight, and only a family that doesn't exist
/// answers 400.
/// </summary>
public sealed class GoogleFontsLiveTests
{
    [LiveFontsFact]
    public async Task A_family_with_no_bold_comes_with_its_regular_weight_only()
    {
        using var client = new GoogleFontsClient();
        var (regular, bold) = await client.FetchAsync("Lobster", CancellationToken.None);
        Assert.NotEmpty(regular);
        Assert.Null(bold);
    }

    [LiveFontsFact]
    public async Task A_family_with_bold_comes_with_both_weights()
    {
        using var client = new GoogleFontsClient();
        var (regular, bold) = await client.FetchAsync("Open Sans", CancellationToken.None);
        Assert.NotEmpty(regular);
        Assert.NotNull(bold);
    }

    [LiveFontsFact]
    public async Task A_family_that_does_not_exist_is_named_as_such()
    {
        using var client = new GoogleFontsClient();
        var error = await Assert.ThrowsAsync<DomainException>(() => client.FetchAsync("Nonexistentfamilyxyz", CancellationToken.None));
        Assert.Equal("font.not_found", error.Code);
    }
}
