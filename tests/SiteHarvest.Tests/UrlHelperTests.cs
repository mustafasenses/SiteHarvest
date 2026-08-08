using SiteHarvest.Helpers;

namespace SiteHarvest.Tests;

public class UrlHelperTests
{
    [Fact]
    public void ToAbsolute_keeps_absolute_urls()
    {
        var abs = "https://example.com/a";
        Assert.Equal(abs, UrlHelper.ToAbsolute(abs, "https://example.com/"));
    }

    [Fact]
    public void ToAbsolute_resolves_relative()
    {
        var result = UrlHelper.ToAbsolute("/urun/1", "https://example.com/liste");
        Assert.Equal("https://example.com/urun/1", result);
    }

    [Fact]
    public void ToAbsolute_null_for_empty() =>
        Assert.Null(UrlHelper.ToAbsolute("  ", "https://example.com"));

    [Fact]
    public void BuildExternalKey_is_stable_and_short()
    {
        var a = UrlHelper.BuildExternalKey("https://Ex.com/X", "img.png", "p0");
        var b = UrlHelper.BuildExternalKey("https://ex.com/x", "IMG.PNG", "p0");
        Assert.Equal(a, b);
        Assert.Equal(24, a.Length);
    }

    [Fact]
    public void BuildExternalKey_differs_when_anchor_differs()
    {
        var a = UrlHelper.BuildExternalKey("https://ex.com", "a.jpg", "0");
        var b = UrlHelper.BuildExternalKey("https://ex.com", "b.jpg", "0");
        Assert.NotEqual(a, b);
    }
}
