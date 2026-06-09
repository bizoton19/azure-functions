namespace StatusMonitor.Core.Services;

public sealed record ParsedUrl(string UrlName, string Url);

public sealed record UrlParseOutcome(IReadOnlyList<ParsedUrl> Urls, IReadOnlyList<string> Errors);

/// <summary>
/// Parses the two bulk-input formats supported by the status-web SPA:
///   1. Spreadsheet exports (CSV): "name,url" rows, with an optional header row.
///   2. Pasted lists: one URL per line, or "name,url" / "name<TAB>url" per line.
/// Rows that cannot be parsed are reported individually so the user can fix
/// just the bad lines instead of re-uploading the whole file.
/// </summary>
public static class UrlListParser
{
    private const int MaxRows = 5000;

    public static UrlParseOutcome Parse(string content)
    {
        var urls = new List<ParsedUrl>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(content))
        {
            errors.Add("The uploaded content is empty.");
            return new UrlParseOutcome(urls, errors);
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Split('\n');
        var lineNumber = 0;

        foreach (var rawLine in lines)
        {
            lineNumber++;
            if (lineNumber > MaxRows)
            {
                errors.Add($"Input truncated at {MaxRows} rows.");
                break;
            }

            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (lineNumber == 1 && IsHeaderRow(line))
            {
                continue;
            }

            var (name, url) = SplitLine(line);
            if (url is null)
            {
                errors.Add($"Line {lineNumber}: could not find a valid http(s) URL in '{Truncate(line)}'.");
                continue;
            }

            name ??= UrlKey.NameFromUrl(url);

            string key;
            try
            {
                key = UrlKey.FromName(name);
            }
            catch (ArgumentException)
            {
                errors.Add($"Line {lineNumber}: '{Truncate(name)}' is not a usable name.");
                continue;
            }

            if (!seenKeys.Add(key))
            {
                continue; // silently dedupe within the same upload
            }

            urls.Add(new ParsedUrl(name, url));
        }

        return new UrlParseOutcome(urls, errors);
    }

    private static bool IsHeaderRow(string line)
    {
        var lowered = line.ToLowerInvariant();
        return !lowered.Contains("http") &&
               (lowered.Contains("url") || lowered.Contains("name") || lowered.Contains("address"));
    }

    private static (string? Name, string? Url) SplitLine(string line)
    {
        var separator = line.Contains('\t') ? '\t' : ',';
        var parts = line.Split(separator, StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"'))
            .Where(p => p.Length > 0)
            .ToArray();

        return parts.Length switch
        {
            0 => (null, null),
            1 => (null, ValidateUrl(parts[0])),
            _ => ResolveNameAndUrl(parts),
        };
    }

    private static (string? Name, string? Url) ResolveNameAndUrl(string[] parts)
    {
        // Accept either column order: "name,url" or "url,name".
        var urlIndex = Array.FindIndex(parts, p => ValidateUrl(p) is not null);
        if (urlIndex < 0)
        {
            return (null, null);
        }

        var name = parts.Where((_, i) => i != urlIndex).FirstOrDefault();
        return (name, ValidateUrl(parts[urlIndex]));
    }

    private static string? ValidateUrl(string candidate)
    {
        var value = candidate.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Contains('.') && !value.Contains(' ') && Uri.TryCreate($"https://{value}", UriKind.Absolute, out _))
            {
                return $"https://{value}";
            }

            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    private static string Truncate(string value) =>
        value.Length <= 60 ? value : value[..57] + "...";
}
