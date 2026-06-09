using StatusMonitor.Core.Services;
using Xunit;

namespace StatusMonitor.Core.Tests;

public class UrlKeyTests
{
    [Theory]
    [InlineData("CPSC Web Site", "cpsc-web-site")]
    [InlineData("  My / Service #1  ", "my-service-1")]
    [InlineData("already-a-key", "already-a-key")]
    public void Normalizes_names_to_rowkey_safe_slugs(string input, string expected)
    {
        Assert.Equal(expected, UrlKey.FromName(input));
    }

    [Fact]
    public void Same_name_different_casing_yields_same_key()
    {
        Assert.Equal(UrlKey.FromName("My Site"), UrlKey.FromName("MY SITE"));
    }

    [Fact]
    public void Rejects_names_with_no_usable_characters()
    {
        Assert.Throws<ArgumentException>(() => UrlKey.FromName("///###"));
    }

    [Fact]
    public void Derives_display_name_from_url()
    {
        Assert.Equal("example.com", UrlKey.NameFromUrl("https://example.com"));
        Assert.Equal("example.com/api/health", UrlKey.NameFromUrl("https://example.com/api/health"));
    }
}
