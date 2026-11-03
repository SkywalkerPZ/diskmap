using System.Text.Json;

namespace DiskMap.Core.Settings;

/// <summary>UndoDays is 0 (Off), 3 or 7; anything else loads as the default.</summary>
public sealed record AppSettings(int UndoDays = 3, bool AlwaysStartElevated = false);

public sealed class SettingsStore(string dir)
{
    private string FilePath => Path.Combine(dir, "settings.json");

    public AppSettings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            return s.UndoDays is 0 or 3 or 7 ? s : s with { UndoDays = 3 };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Atomic: writes a temp file, then replaces.</summary>
    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }
}
