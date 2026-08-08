using SiteHarvest.Helpers;

namespace SiteHarvest.Tests;

public class SelectorHelperTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  a.card  ", "a.card")]
    public void Sanitize_trims_or_nulls(string? input, string? expected) =>
        Assert.Equal(expected, SelectorHelper.Sanitize(input));

    [Theory]
    [InlineData(
        "div.list > a.row:nth-of-type(3)",
        "div.list > a.row")]
    [InlineData(
        "ul > li:nth-child(2) > a.title",
        "ul > li > a.title")]
    [InlineData(
        "div#list > div:nth-of-type(1).row > a > img",
        "div#list > div.row > a > img")]
    [InlineData(
        "div.collection > div:nth-of-type(1).row > a > p",
        "div.collection > div.row > a > p")]
    public void GeneralizeListSelector_strips_nth_indices(string input, string expected) =>
        Assert.Equal(expected, SelectorHelper.GeneralizeListSelector(input));

    [Theory]
    [InlineData("a.row:nth-of-type(1)", null, true)]
    [InlineData("div.link", null, false)]
    [InlineData("a", "Open details", false)]
    [InlineData("nav > a.home", "Home", false)]
    [InlineData("ul > li:nth-child(2) > a", null, true)]
    public void LooksLikeListItem_only_from_nth_index(string selector, string? text, bool expected) =>
        Assert.Equal(expected, SelectorHelper.LooksLikeListItem(selector, text));

    [Theory]
    [InlineData(
        "div.body > p:nth-of-type(1).heading",
        "p.heading")]
    [InlineData("img", "img")]
    public void LeafSelector_takes_last_segment(string input, string expected) =>
        Assert.Equal(expected, SelectorHelper.LeafSelector(input));

    [Fact]
    public void InferRepeatingCardSelector_uses_longest_common_prefix()
    {
        var card = SelectorHelper.InferRepeatingCardSelector(new[]
        {
            "div:nth-of-type(2).wrap > div > div:nth-of-type(1).row > div:nth-of-type(2).body > p:nth-of-type(1).heading",
            "div:nth-of-type(2).wrap > div > div:nth-of-type(1).row > div:nth-of-type(2).body > p:nth-of-type(2).copy",
        });
        Assert.Equal(
            "div.wrap > div > div.row > div.body",
            card);
    }

    [Fact]
    public void InferRepeatingCardSelector_single_field_keeps_leaf_path()
    {
        var card = SelectorHelper.InferRepeatingCardSelector(new[]
        {
            "div.list > div:nth-of-type(1).row > a > p.title",
        });
        Assert.Equal("div.list > div.row > a > p.title", card);
    }

    [Fact]
    public void InferRepeatingCardSelector_returns_null_without_nth()
    {
        var card = SelectorHelper.InferRepeatingCardSelector(new[]
        {
            "main > article > h1",
            "main > article > p",
        });
        Assert.Null(card);
    }
}
