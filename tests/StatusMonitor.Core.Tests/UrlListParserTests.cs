using StatusMonitor.Core.Services;
using Xunit;

namespace StatusMonitor.Core.Tests;

public class UrlListParserTests
{
    [Fact]
    public void Parses_csv_with_header_row()
    {
        var csv = "name,url\nCPSC Web Site,https://www.cpsc.gov\nRecalls,https://www.cpsc.gov/Recalls";

        var outcome = UrlListParser.Parse(csv);

        Assert.Empty(outcome.Errors);
        Assert.Equal(2, outcome.Urls.Count);
        Assert.Equal("CPSC Web Site", outcome.Urls[0].UrlName);
        Assert.Equal("https://www.cpsc.gov/", outcome.Urls[0].Url);
    }

    [Fact]
    public void Parses_pasted_bare_urls_and_derives_names()
    {
        var pasted = "https://example.com/health\nhttps://api.example.com";

        var outcome = UrlListParser.Parse(pasted);

        Assert.Empty(outcome.Errors);
        Assert.Equal(2, outcome.Urls.Count);
        Assert.Equal("example.com/health", outcome.Urls[0].UrlName);
        Assert.Equal("api.example.com", outcome.Urls[1].UrlName);
    }

    [Fact]
    public void Accepts_url_in_either_column_order()
    {
        var csv = "https://example.com,Example Site";

        var outcome = UrlListParser.Parse(csv);

        var parsed = Assert.Single(outcome.Urls);
        Assert.Equal("Example Site", parsed.UrlName);
        Assert.Equal("https://example.com/", parsed.Url);
    }

    [Fact]
    public void Adds_https_scheme_to_schemeless_hosts()
    {
        var outcome = UrlListParser.Parse("My Site,example.com");

        var parsed = Assert.Single(outcome.Urls);
        Assert.Equal("https://example.com", parsed.Url);
    }

    [Fact]
    public void Reports_invalid_rows_with_line_numbers()
    {
        var csv = "Good,https://example.com\nthis is not a url at all";

        var outcome = UrlListParser.Parse(csv);

        Assert.Single(outcome.Urls);
        var error = Assert.Single(outcome.Errors);
        Assert.Contains("Line 2", error);
    }

    [Fact]
    public void Dedupes_rows_that_normalize_to_the_same_key()
    {
        var csv = "My Site,https://example.com\nmy site,https://example.com/other";

        var outcome = UrlListParser.Parse(csv);

        Assert.Single(outcome.Urls);
    }

    [Fact]
    public void Handles_tab_separated_paste_from_spreadsheets()
    {
        var pasted = "Example\thttps://example.com";

        var outcome = UrlListParser.Parse(pasted);

        var parsed = Assert.Single(outcome.Urls);
        Assert.Equal("Example", parsed.UrlName);
    }

    [Fact]
    public void Empty_content_is_an_error()
    {
        var outcome = UrlListParser.Parse("   ");

        Assert.Empty(outcome.Urls);
        Assert.NotEmpty(outcome.Errors);
    }
}
