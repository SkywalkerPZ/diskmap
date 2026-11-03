using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskMap.Core.Removal;

/// <summary>Reads English DISM /AnalyzeComponentStore output; any other language yields null (no estimate shown).</summary>
public static class ComponentStoreEstimate
{
    private static readonly Regex Backups = Line("Backups and Disabled Features");
    private static readonly Regex Cache = Line("Cache and Temporary Data");

    // The labels are English, but DISM formats the number with the user's locale: "6.10", "6,10", "1,234.50", "1.234,50", "1 234,50".
    private static Regex Line(string label) =>
        new(Regex.Escape(label) + @"\s*:\s*([0-9][0-9.,  ']*)\s*(KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Low = cache and temporary data; High = that plus backups and disabled features.</summary>
    public static (long Low, long High)? Parse(string dismOutput)
    {
        if (Bytes(Cache.Match(dismOutput)) is not { } low || Bytes(Backups.Match(dismOutput)) is not { } backups) return null;
        return ((long)Math.Round(low), (long)Math.Round(low + backups));
    }

    /// <summary>
    /// Locale-neutral number: the last '.' or ',' is the decimal separator when one or two digits follow it (DISM prints
    /// two decimals); every other separator is digit grouping. "1,23" = 1.23, "1,234" = 1234.
    /// </summary>
    internal static double? Number(string s)
    {
        s = s.TrimEnd('.', ',', ' ', ' ', '\'');
        var last = s.LastIndexOfAny(['.', ',']);
        var decimals = last >= 0 && s.Length - last - 1 is 1 or 2;
        var whole = new string((decimals ? s[..last] : s).Where(char.IsAsciiDigit).ToArray());
        var text = decimals ? whole + "." + s[(last + 1)..] : whole;
        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? Bytes(Match m)
    {
        if (!m.Success || Number(m.Groups[1].Value) is not { } v) return null;
        var pow = m.Groups[2].Value.ToUpperInvariant() switch { "KB" => 1, "MB" => 2, "GB" => 3, _ => 4 };
        return v * Math.Pow(1024, pow);
    }
}
