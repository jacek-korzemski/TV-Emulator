using System.Text.RegularExpressions;

namespace TvEmulator.Services;

public static partial class ProxyRewrite
{
    public static string RewriteOrigin(string text, Uri origin, string localBase)
    {
        var authority = origin.IsDefaultPort ? origin.Host : origin.Host + ":" + origin.Port;
        return OriginPattern(authority).Replace(text, localBase);
    }

    public static string RewriteCookie(string cookie)
    {
        var kept = new List<string>();
        foreach (var raw in cookie.Split(';'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;
            var name = part.Split('=', 2)[0].Trim();
            if (name.Equals("Secure", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Domain", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals("SameSite", StringComparison.OrdinalIgnoreCase)
                && part.Contains("None", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add("SameSite=Lax");
                continue;
            }

            kept.Add(part);
        }

        return string.Join("; ", kept);
    }

    private static Regex OriginPattern(string authority) =>
        new($@"(?i)(?:https?:)?(?:\\?/\\?/){Regex.Escape(authority)}(?![A-Za-z0-9.-])", RegexOptions.CultureInvariant);
}
