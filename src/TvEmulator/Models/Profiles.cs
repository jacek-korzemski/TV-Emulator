namespace TvEmulator.Models;

public sealed class TvProfile
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Player { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public Viewport Viewport { get; set; } = new();
    public string PlatformVersion { get; set; } = "";
    public KeyMap Keys { get; set; } = new();
    public Quirks Quirks { get; set; } = new();
    public int? MemoryMb { get; set; }
    public int? CpuPercent { get; set; }
    public ProductFacts Product { get; set; } = new();
}

public sealed class Viewport
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
}

public sealed class KeyMap
{
    public int Back { get; set; } = 10009;
    public int Enter { get; set; } = 13;
    public int Left { get; set; } = 37;
    public int Up { get; set; } = 38;
    public int Right { get; set; } = 39;
    public int Down { get; set; } = 40;
    public int Red { get; set; } = 403;
    public int Green { get; set; } = 404;
    public int Yellow { get; set; } = 405;
    public int Blue { get; set; } = 406;
}

public sealed class Quirks
{
    public List<string> StripCss { get; set; } = new();
    public List<string> DeleteGlobals { get; set; } = new();
}

public sealed class ProductFacts
{
    public string Model { get; set; } = "";
    public string ModelCode { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string Duid { get; set; } = "";
    public bool Uhd { get; set; }
    public string Brand { get; set; } = "";
}

public sealed class EngineBuild
{
    public string Id { get; set; } = "";
    public int Milestone { get; set; }
    public int Revision { get; set; }
    public string VersionLabel { get; set; } = "";
    public string Archive { get; set; } = "";
    public string Kind { get; set; } = "chromium";
    public string ElectronVersion { get; set; } = "";
    public List<string> Args { get; set; } = new();
}
