namespace TvEmulator.Services;

public static class CdnProxy
{
    public const string Prefix = "/__emulator/cdn/";

    public static bool TryParse(string? rawUrl, out Uri target)
    {
        target = null!;
        if (string.IsNullOrEmpty(rawUrl) || !rawUrl.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var rest = rawUrl.Substring(Prefix.Length);
        var schemeSlash = rest.IndexOf('/');
        if (schemeSlash <= 0)
            return false;

        var scheme = rest.Substring(0, schemeSlash);
        if (scheme is not ("http" or "https"))
            return false;

        var afterScheme = rest.Substring(schemeSlash + 1);
        var hostSlash = afterScheme.IndexOf('/');
        string host;
        string pathAndQuery;
        if (hostSlash < 0)
        {
            var query = afterScheme.IndexOf('?');
            if (query >= 0)
            {
                host = afterScheme.Substring(0, query);
                pathAndQuery = "/" + afterScheme.Substring(query);
            }
            else
            {
                host = afterScheme;
                pathAndQuery = "/";
            }
        }
        else
        {
            host = afterScheme.Substring(0, hostSlash);
            pathAndQuery = afterScheme.Substring(hostSlash);
            if (pathAndQuery.Length == 0)
                pathAndQuery = "/";
        }

        if (!IsAllowedHost(host))
            return false;

        if (!Uri.TryCreate(scheme + "://" + host + pathAndQuery, UriKind.Absolute, out var parsed) || parsed is null)
            return false;

        if (!IsAllowedTarget(parsed) || !parsed.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            return false;

        target = parsed;
        return true;
    }

    public static bool TryFollow(Uri current, Uri? location, out Uri next)
    {
        next = null!;
        if (location is null)
            return false;

        Uri absolute;
        try
        {
            absolute = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!IsAllowedTarget(absolute))
            return false;

        next = absolute;
        return true;
    }

    public static bool IsAllowedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        host = host.Trim().TrimEnd('.');
        if (host.IndexOfAny([':', '@', '\\', '/', ' ', '%']) >= 0)
            return false;

        return host.Equals("pluscdn.pl", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".pluscdn.pl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedTarget(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var expectedPort = uri.Scheme == "https" ? 443 : 80;
        if (uri.Port != expectedPort)
            return false;

        return IsAllowedHost(uri.Host);
    }
}
