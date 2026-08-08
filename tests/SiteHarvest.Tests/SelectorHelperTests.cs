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
        "div.products > a.card:nth-of-type(3)",
        "div.products > a.card")]
    [InlineData(
        "ul > li:nth-child(2) > a.title",
        "ul > li > a.title")]
    [InlineData(
        "div#list > div:nth-of-type(1).item > a > img",
        "div#list > div.item > a > img")]
    [InlineData(
        "div.collection-product-list > div:nth-of-type(1).item > a > p",
        "div.collection-product-list > div.item > a > p")]
    public void GeneralizeListSelector_strips_nth_indices(string input, string expected) =>
        Assert.Equal(expected, SelectorHelper.GeneralizeListSelector(input));

    [Theory]
    [InlineData("a.card:nth-of-type(1)", null, true)]
    [InlineData("div.product-link", null, true)]
    [InlineData("a", "Ürün detay", true)]
    [InlineData("nav > a.home", "Ana sayfa", false)]
    public void LooksLikeListItem_heuristics(string selector, string? text, bool expected) =>
        Assert.Equal(expected, SelectorHelper.LooksLikeListItem(selector, text));
}
