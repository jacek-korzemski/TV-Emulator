using System.Text.RegularExpressions;

namespace TvEmulator.Services;

/// <summary>
/// Checks a launch address against the hosts written in the shelf's own catalog.js.
/// The catalog stays on this PC. This type does not name any service.
/// </summary>
public static partial class TvAppShelf
{
    public static bool TryParseLaunchUrl(string? raw, IReadOnlySet<string> allowedHosts, out Uri page)
    {
        page = null!;
        if (!TryParseHttps(raw, out var uri))
            return false;

        var host = HostOf(uri);
        if (!allowedHosts.Contains(host))
            return false;

        page = uri;
        return true;
    }

    public static HashSet<string> ReadHosts(string directory)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(directory))
            return hosts;

        var path = Path.Combine(directory, "catalog.js");
        if (!File.Exists(path))
            return hosts;

        foreach (Match match in HttpsUrl().Matches(File.ReadAllText(path)))
        {
            if (TryParseHttps(match.Value, out var uri))
                hosts.Add(HostOf(uri));
        }

        return hosts;
    }

    private static bool TryParseHttps(string? raw, out Uri page)
    {
        page = null!;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 2000)
            return false;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || uri is null)
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (uri.Port != 443)
            return false;
        if (uri.HostNameType != UriHostNameType.Dns)
            return false;

        var host = HostOf(uri);
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        page = uri;
        return true;
    }

    private static string HostOf(Uri uri) => uri.Host.TrimEnd('.');

    [GeneratedRegex(@"https://[^""'\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex HttpsUrl();
}
