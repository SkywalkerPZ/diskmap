using DiskMap.Core.Rules;

namespace DiskMap.Core.Tests;

public class RuleLoaderTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Rules", "data");
    private static Rule Get(string id) => RuleLoader.LoadAll(DataDir).Single(r => r.Id == id);

    [Fact]
    public void LoadAll_LoadsTheShippedRules()
    {
        var rules = RuleLoader.LoadAll(DataDir);
        Assert.Equal(11, rules.Count);
        Assert.Equal(11, rules.Select(r => r.Id).Distinct().Count());
        // every shipped path must expand cleanly: no leftover token, no collapsed or doubled separators
        using var fx = new Fixture();
        var sep = Path.DirectorySeparatorChar;
        foreach (var p in rules.SelectMany(r => r.Paths))
        {
            var e = fx.User.Expand(p, Path.Combine(fx.Root, "Windows"));
            Assert.DoesNotContain("{", e);
            Assert.DoesNotContain("" + sep + sep, e);
        }
        Assert.All(rules.SelectMany(r => r.Paths).Where(p => p.StartsWith("C:")), p => Assert.StartsWith("C:" + sep, p));
        Assert.Contains(rules.Single(r => r.Id == "dev-caches").Paths, p => p.Contains(sep + "npm-cache"));
    }

    [Fact]
    public void ShippedRules_MatchSpecLabels()
    {
        var wu = Get("windows-update");
        Assert.Equal(SafetyLabel.Safe, wu.Label);
        Assert.Equal(RemovalMethod.DismComponentCleanup, wu.Method);
        Assert.True(wu.NeedsAdmin);
        var h = Get("hibernation");
        Assert.Equal(SafetyLabel.SafeIfNoHibernate, h.Label);
        Assert.Equal(RemovalMethod.PowercfgHibernateOff, h.Method);
        Assert.True(h.NeedsAdmin);
        var oi = Get("old-installers");
        Assert.Equal(SafetyLabel.CheckFirst, oi.Label);
        Assert.Equal(new[] { ".exe", ".msi", ".zip" }, oi.Extensions);
        Assert.Equal(90, oi.MinAgeDays);
        var rb = Get("recycle-bin");
        Assert.Equal(SafetyLabel.AlreadyDeleted, rb.Label);
        Assert.Equal(RemovalMethod.EmptyRecycleBin, rb.Method);
        Assert.Equal(SafetyLabel.Rebuildable, Get("dev-caches").Label);
        Assert.True(Get("temp").Undoable);
        Assert.False(wu.Undoable);
    }

    [Theory]
    [InlineData("windows-update", "You can no longer roll back the last update")]
    [InlineData("temp", "Apps recreate them")]
    [InlineData("windows-temp", "Apps recreate them")]
    [InlineData("browser-cache", "Sites load a bit slower once")]
    [InlineData("hibernation", "Hibernate and Fast Startup stop working")]
    [InlineData("dev-caches", "Comes back on the next install or build")]
    [InlineData("shader-cache", "Games stutter a little on first launch")]
    [InlineData("old-installers", "It might be your only copy")]
    [InlineData("recycle-bin", "Emptying it is permanent")]
    [InlineData("crash-dumps", "They're only useful for debugging a past crash")]
    [InlineData("crash-dumps-system", "They're only useful for debugging a past crash")]
    public void ShippedRules_ConsequenceTextMatchesSpec(string id, string consequence) =>
        Assert.Equal(consequence, Get(id).Consequence);

    [Fact]
    public void LoadAll_RejectsUnknownToken()
    {
        using var fx = new Fixture();
        File.WriteAllText(Path.Combine(fx.Dir("rules"), "bad.json"), """
            {"id":"bad","category":"c","title":"t","label":"Safe","explanation":"e","consequence":"c",
             "method":"Quarantine","needsAdmin":false,"paths":["{Bogus}/x"]}
            """);
        var ex = Assert.Throws<InvalidDataException>(() => RuleLoader.LoadAll(Path.Combine(fx.Root, "rules")));
        Assert.Contains("bad.json", ex.Message);
    }

    [Theory]
    [InlineData("\"label\":\"Nope\",\"method\":\"Quarantine\"")]
    [InlineData("\"label\":\"Safe\"")] // missing method
    public void LoadAll_RejectsBadEnumOrMissingField(string body)
    {
        using var fx = new Fixture();
        File.WriteAllText(Path.Combine(fx.Dir("rules"), "bad2.json"),
            "{\"id\":\"x\",\"category\":\"c\",\"title\":\"t\",\"explanation\":\"e\",\"consequence\":\"c\",\"needsAdmin\":false," + body + "}");
        var ex = Assert.Throws<InvalidDataException>(() => RuleLoader.LoadAll(Path.Combine(fx.Root, "rules")));
        Assert.Contains("bad2.json", ex.Message);
    }

    [Fact]
    public void UserContext_ArgsRoundTrip()
    {
        using var fx = new Fixture();
        Assert.Equal(fx.User, UserContext.FromArgs(fx.User.ToArgs()));
        Assert.Null(UserContext.FromArgs(["--user-sid", "x"]));
    }

    [Fact]
    public void UserContext_Current_IsPopulated()
    {
        var c = UserContext.Current();
        Assert.StartsWith("S-1-", c.Sid);
        Assert.True(Directory.Exists(c.ProfilePath));
        Assert.False(string.IsNullOrEmpty(c.Downloads));
    }
}
