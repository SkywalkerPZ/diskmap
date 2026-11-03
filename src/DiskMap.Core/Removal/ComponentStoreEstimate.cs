using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskMap.Core.Removal;

/// <summary>Reads English DISM /AnalyzeComponentStore output; any other language yields null (no estimate shown).</summary>
public static class ComponentStoreEstimate
{
    private static readonly Regex Backups = Line("Backups and Disabled Features");
    private static readonly Regex Cache = Line("Cache and Temporary Data");

    private static Regex Line(string label) =>
        new(Regex.Escape(label) + @"\s*:\s*([0-9][0-9.,]*)\s*(KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Low = cache and temporary data; High = that plus backups and disabled features.</summary>
    public static (long Low, long High)? Parse(string dismOutput)
    {
        if (Bytes(Cache.Match(dismOutput)) is not { } low || Bytes(Backups.Match(dismOutput)) is not { } backups) return null;
        return ((long)Math.Round(low), (long)Math.Round(low + backups));
    }

    private static double? Bytes(Match m)
    {
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        var pow = m.Groups[2].Value.ToUpperInvariant() switch { "KB" => 1, "MB" => 2, "GB" => 3, _ => 4 };
        return v * Math.Pow(1024, pow);
    }
}
