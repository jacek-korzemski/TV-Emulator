namespace TvEmulator.Services;

public static class AppFiles
{
    public static string? Resolve(string root, string absolutePath)
    {
        var relative = absolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0)
            relative = "index.html";

        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relative));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return combined;
    }

    public static bool IsPlatformScript(string absolutePath)
    {
        if (absolutePath.Contains("$WEBAPIS", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = Path.GetFileName(absolutePath.TrimEnd('/'));
        return name.Equals("webapis.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("webOSTV.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("webOS.js", StringComparison.OrdinalIgnoreCase);
    }
}
