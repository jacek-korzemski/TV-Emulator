namespace TvEmulator.Services;

public sealed class SessionLog : IDisposable
{
    private readonly object _gate = new();

    public SessionLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
    }

    public string FilePath { get; }

    public void Write(string line)
    {
        var single = line.Replace("\r", " ").Replace("\n", " ");
        var limit = single.StartsWith("rpc ", StringComparison.Ordinal) ? 2_000_000 : 2000;
        if (single.Length > limit)
            single = single.Substring(0, limit);
        var text = DateTime.Now.ToString("HH:mm:ss.fff") + " " + single + Environment.NewLine;
        lock (_gate)
            File.AppendAllText(FilePath, text);
    }

    public void Dispose()
    {
    }
}
