namespace TvEmulator.Debug;

public sealed class DebugStatus
{
    public bool Active { get; set; }
    public bool Diagnostics { get; set; }
    public string Profile { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Engine { get; set; } = "";
    public string LogFile { get; set; } = "";
    public CdpFlags Cdp { get; set; } = new();
    public List<string> Urls { get; set; } = new();
    public int Port { get; set; }
}

public sealed class CdpFlags
{
    public string Engine { get; set; } = "off";
    public string Video { get; set; } = "off";
}

public sealed class ConsoleLine
{
    public long Id { get; set; }
    public string Source { get; set; } = "engine";
    public string Level { get; set; } = "info";
    public string Text { get; set; } = "";
    public string Url { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Stack { get; set; } = "";
    public string Time { get; set; } = "";
}

public sealed class NetworkCall
{
    public long Id { get; set; }
    public string Source { get; set; } = "engine";
    public string CdpId { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public string Type { get; set; } = "other";
    public string RawType { get; set; } = "";
    public int Status { get; set; }
    public string Mime { get; set; } = "";
    public string Error { get; set; } = "";
    public string Time { get; set; } = "";
    public long? DurationMs { get; set; }
    public long? EncodedSize { get; set; }
    public string RequestHeaders { get; set; } = "";
    public string ResponseHeaders { get; set; } = "";
    public string? RequestBody { get; set; }
    public string? ResponseBody { get; set; }
    public bool HasRequestBody { get; set; }
    public bool HasResponseBody { get; set; }
    public bool BodySkipped { get; set; }
    public string BodySkipReason { get; set; } = "";
    public bool BodyTruncated { get; set; }
    public double StartedMono { get; set; }
    public bool HasStart { get; set; }
}

public sealed class MemorySample
{
    public string Source { get; set; } = "";
    public string Time { get; set; } = "";
    public long? Used { get; set; }
    public long? Total { get; set; }
    public long? Limit { get; set; }
    public int Nodes { get; set; }
}

public sealed class HeapClass
{
    public int Count { get; set; }
    public long SelfSize { get; set; }
}

public sealed class HeapRow
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public long SelfSize { get; set; }
    public int CountDelta { get; set; }
    public long SizeDelta { get; set; }
}

public sealed class HeapReport
{
    public string Source { get; set; } = "";
    public string At { get; set; } = "";
    public int Classes { get; set; }
    public long TotalSelf { get; set; }
    public bool HasPrevious { get; set; }
    public string? FilePath { get; set; }
    public List<HeapRow> Retained { get; set; } = new();
    public List<HeapRow> Grew { get; set; } = new();
    public List<HeapRow> Freed { get; set; } = new();
}

public sealed class HeapGcSample
{
    public string Source { get; set; } = "";
    public string Time { get; set; } = "";
    public long Count { get; set; }
    public long Size { get; set; }
}
