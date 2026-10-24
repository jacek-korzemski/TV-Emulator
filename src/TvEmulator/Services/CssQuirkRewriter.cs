using System.Text.RegularExpressions;

namespace TvEmulator.Services;

public static partial class CssQuirkRewriter
{
    public static string Strip(string css, IReadOnlyList<string> properties)
    {
        if (string.IsNullOrEmpty(css) || properties.Count == 0)
            return css;

        foreach (var property in properties)
        {
            if (string.IsNullOrWhiteSpace(property))
                continue;
            css = PropertyPattern(Regex.Escape(property.Trim())).Replace(css, "");
        }

        return css;
    }

    public static string StripStyleElements(string html, IReadOnlyList<string> properties)
    {
        if (properties.Count == 0)
            return html;

        return StyleBlock().Replace(html, match =>
            match.Groups[1].Value + Strip(match.Groups[2].Value, properties) + match.Groups[3].Value);
    }

    private static Regex PropertyPattern(string escapedName) =>
        new($@"(?i)(?<![-\w]){escapedName}\s*:\s*[^;}}]*;?", RegexOptions.CultureInvariant);

    [GeneratedRegex("(?is)(<style\\b[^>]*>)(.*?)(</style>)")]
    private static partial Regex StyleBlock();
}
