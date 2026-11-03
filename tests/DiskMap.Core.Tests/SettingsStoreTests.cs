using DiskMap.Core.Settings;

namespace DiskMap.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Settings_InvalidJson_LoadsDefaults()
    {
        using var fx = new Fixture();
        File.WriteAllText(Path.Combine(fx.Root, "settings.json"), "{ not json");
        Assert.Equal(new AppSettings(), new SettingsStore(fx.Root).Load());
        Assert.Equal(new AppSettings(), new SettingsStore(Path.Combine(fx.Root, "missing")).Load());
    }

    [Theory]
    [InlineData(1)] [InlineData(5)] [InlineData(-1)] [InlineData(30)]
    public void Settings_UndoDaysOutsideAllowedSet_LoadsDefault3(int days)
    {
        using var fx = new Fixture();
        File.WriteAllText(Path.Combine(fx.Root, "settings.json"), "{\"UndoDays\":" + days + ",\"AlwaysStartElevated\":true}");
        Assert.Equal(new AppSettings(3, true), new SettingsStore(fx.Root).Load());
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(7)]
    public void Settings_RoundTrips_AndOverwrites(int days)
    {
        using var fx = new Fixture();
        var store = new SettingsStore(Path.Combine(fx.Root, "sub"));
        store.Save(new AppSettings(7, false));
        store.Save(new AppSettings(days, true));
        Assert.Equal(new AppSettings(days, true), store.Load());
        Assert.False(File.Exists(Path.Combine(fx.Root, "sub", "settings.json.tmp")));
    }
}
