using System.Globalization;
using TvEmulator.Models;

namespace TvEmulator.Services;

/// <summary>
/// Rough single-thread pace of a television, expressed as GHz of a 2022–2024 desktop core.
/// A guess used by the calculator, not a measurement from a set.
/// </summary>
public static class TvPower
{
    public const int MaxMemoryMb = 1_048_576;

    public readonly record struct Hint(string Text, int? Percent);

    public static double DesktopGhz(EngineBuild engine)
    {
        if (string.Equals(engine.Kind, "firefox", StringComparison.OrdinalIgnoreCase) || engine.Milestone <= 49)
            return 0.45;
        if (engine.Milestone <= 56)
            return 0.55;
        if (engine.Milestone <= 63)
            return 0.70;
        if (engine.Milestone <= 88)
            return 1.30;
        return 2.60;
    }

    /// <summary>
    /// Percent of one logical thread that lands near 1:1. Thread count is not an input:
    /// the page runs on one core, and extra host threads do not make that core slower.
    /// </summary>
    public static int? PercentForClock(double televisionGhz, double clockGhz)
    {
        if (clockGhz <= 0 || televisionGhz <= 0)
            return null;
        var percent = (int)Math.Round(100.0 * televisionGhz / clockGhz, MidpointRounding.AwayFromZero);
        if (percent > 100)
            return null;
        if (percent < 1)
            return 1;
        return percent;
    }

    public static Hint Describe(EngineBuild? engine, string clockText, string threadsText)
    {
        if (engine is null)
            return new Hint("Ten profil nie wskazuje silnika, więc nie da się policzyć 1:1.", null);
        if (!TryParseClock(clockText, out var clock))
            return new Hint("Wpisz taktowanie jednego rdzenia w GHz.", null);

        var percent = PercentForClock(DesktopGhz(engine), clock);
        if (percent is null)
            return new Hint("Jeden wątek o tym taktowaniu nie jest szybszy od tego telewizora. Limit procentowy go do niego nie zbliży.", null);

        var text = "Dla tego profilu 1:1 to około " + percent + "% jednego wątku.";
        if (TryParseThreads(threadsText, out var threads))
            text += " " + ShareSentence(percent.Value, threads);
        return new Hint(text, percent);
    }

    public static string ShareSentence(int percent, int threads)
    {
        var share = percent / (double)threads;
        var text = share.ToString("0.0", CultureInfo.GetCultureInfo("pl-PL"));
        return percent + "% jednego wątku przy " + threads + " wątkach to " + text + "% całego procesora.";
    }

    public static bool TryParseClock(string text, out double ghz)
    {
        ghz = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out ghz)
            && ghz > 0
            && ghz < 20;
    }

    public static bool TryParseThreads(string text, out int threads)
    {
        threads = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out threads)
            && threads is >= 1 and <= 4096;
    }

    public static bool TryReadLimit(string text, int minimum, int maximum, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum)
            return false;
        value = parsed;
        return true;
    }
}
