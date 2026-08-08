using SiteHarvest.Models;

namespace SiteHarvest.Tests;

public class FieldTypesTests
{
    [Theory]
    [InlineData(null, "text")]
    [InlineData(" TEXT ", "text")]
    [InlineData("Image", "image")]
    [InlineData("URL", "url")]
    public void Normalize_accepts_known_types(string? input, string expected) =>
        Assert.Equal(expected, FieldTypes.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("html")]
    public void Normalize_rejects_unknown(string input) =>
        Assert.Throws<ArgumentException>(() => FieldTypes.Normalize(input));
}
