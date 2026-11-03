using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace DiskMap.App;

/// <summary>UI copy from Strings/en-US/Resources.resw.</summary>
internal static class Str
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string name) => Loader.GetString(name);

    public static string Format(string name, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(name), args);
}

/// <summary>The one size format used everywhere: "1.2 GB", "340 MB", "512 bytes".</summary>
internal static class SizeFormat
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes) => $"{Number(bytes, UnitOf(bytes))} {Units[UnitOf(bytes)]}";

    /// <summary>"1.2–3.4 GB": both ends in the larger value's unit.</summary>
    public static string Range(long low, long high)
    {
        var u = UnitOf(high);
        return $"{Number(low, u)}–{Number(high, u)} {Units[u]}";
    }

    private static int UnitOf(long bytes)
    {
        int u = 0;
        for (double v = Math.Abs((double)bytes); v >= 1024 && u < Units.Length - 1; v /= 1024) u++;
        return u;
    }

    private static string Number(long bytes, int unit)
    {
        if (unit == 0) return bytes.ToString("N0", CultureInfo.CurrentCulture);
        var v = bytes / Math.Pow(1024, unit);
        return v.ToString(v < 100 ? "0.#" : "0", CultureInfo.CurrentCulture);
    }
}
