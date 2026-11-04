namespace TvEmulator.Services;

public static class RepoPaths
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (IsSolutionRoot(dir.FullName))
                return dir.FullName;
            if (IsPortableRoot(dir.FullName))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Nie znaleziono TvEmulator.sln ani katalogu portable (engines\\builds.json i profiles) powyżej katalogu programu.");
    }

    public static string DataRoot()
    {
        var root = Root();
        if (IsSolutionRoot(root))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TvEmulator");
        }

        return Path.Combine(root, "data");
    }

    private static bool IsSolutionRoot(string dir) =>
        File.Exists(Path.Combine(dir, "TvEmulator.sln"));

    private static bool IsPortableRoot(string dir) =>
        File.Exists(Path.Combine(dir, "engines", "builds.json"))
        && Directory.Exists(Path.Combine(dir, "profiles"));
}
