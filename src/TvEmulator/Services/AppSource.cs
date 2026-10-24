namespace TvEmulator.Services;

public readonly record struct AppSource(string? Directory, Uri? Page)
{
    public bool IsRemote => Page is not null;

    public string LocalStartPath => Page is null ? "/" : Page.PathAndQuery + Page.Fragment;

    public Uri? Origin => Page is null ? null : new Uri(Page.GetLeftPart(UriPartial.Authority));

    public static AppSource Parse(string text)
    {
        var trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return new AppSource(null, uri);

        return new AppSource(Path.GetFullPath(trimmed), null);
    }
}
