using System.Text;

namespace StatusMonitor.Core.Services;

/// <summary>
/// Normalizes a user-supplied URL name into a key that is safe to use as an
/// Azure Table Storage RowKey (no '/', '\', '#', '?', control chars) and stable
/// across re-imports so uploads are idempotent.
/// </summary>
public static class UrlKey
{
    private const int MaxLength = 128;

    public static string FromName(string urlName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlName);

        var builder = new StringBuilder(urlName.Length);
        foreach (var c in urlName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var key = builder.ToString().Trim('-');
        if (key.Length == 0)
        {
            throw new ArgumentException($"URL name '{urlName}' contains no usable characters.", nameof(urlName));
        }

        return key.Length <= MaxLength ? key : key[..MaxLength];
    }

    /// <summary>Derives a display name from a bare URL when the user did not supply one.</summary>
    public static string NameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.Trim('/');
            return string.IsNullOrEmpty(path) ? uri.Host : $"{uri.Host}/{path}";
        }

        return url;
    }
}
