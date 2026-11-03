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
        Assert.Equal(12, rules.Count);
        Assert.Equal(12, rules.Select(r => r.Id).Distinct().Count());
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

    private static readonly string[] None = [];
    private static readonly string[] Csproj = ["*.csproj", "*.fsproj", "*.vbproj"];

    // '/' stands for the path separator so no backslash escaping can go wrong.
    private static string[] P(params string[] p) => p.Select(s => s.Replace('/', Path.DirectorySeparatorChar)).ToArray();

    public static TheoryData<string, SafetyLabel, RemovalMethod, bool, string[], string[], string[], string[], int?> Expected => new()
    {
        { "windows-update", SafetyLabel.Safe, RemovalMethod.DismComponentCleanup, true, P("{Windows}/WinSxS"), None, None, None, null },
        { "temp", SafetyLabel.Safe, RemovalMethod.Quarantine, false, P("{Temp}"), None, None, None, null },
        { "windows-temp", SafetyLabel.Safe, RemovalMethod.Quarantine, true, P("{Windows}/Temp"), None, None, None, null },
        { "browser-cache", SafetyLabel.Safe, RemovalMethod.Quarantine, false, P(
            "{LocalAppData}/Google/Chrome/User Data/*/Cache", "{LocalAppData}/Google/Chrome/User Data/*/Code Cache",
            "{LocalAppData}/Microsoft/Edge/User Data/*/Cache", "{LocalAppData}/Microsoft/Edge/User Data/*/Code Cache",
            "{LocalAppData}/Mozilla/Firefox/Profiles/*/cache2"), None, None, None, null },
        { "hibernation", SafetyLabel.SafeIfNoHibernate, RemovalMethod.PowercfgHibernateOff, true, P("C:/hiberfil.sys"), None, None, None, null },
        { "dev-caches", SafetyLabel.Rebuildable, RemovalMethod.Quarantine, false,
            P("{LocalAppData}/npm-cache", "{LocalAppData}/pip/Cache", "{UserProfile}/.nuget/packages"),
            ["node_modules", ".gradle"], ["package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "bun.lock",
                "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"], None, null },
        { "dotnet-build", SafetyLabel.Rebuildable, RemovalMethod.Quarantine, false, None, ["bin", "obj"], Csproj, None, null },
        { "shader-cache", SafetyLabel.Safe, RemovalMethod.Quarantine, false, P(
            "{LocalAppData}/NVIDIA/DXCache", "{LocalAppData}/NVIDIA/GLCache", "{LocalAppData}/AMD/DxCache", "{LocalAppData}/D3DSCache"), None, None, None, null },
        { "old-installers", SafetyLabel.CheckFirst, RemovalMethod.Quarantine, false, P("{Downloads}"), None, None, [".exe", ".msi", ".zip"], 90 },
        { "recycle-bin", SafetyLabel.AlreadyDeleted, RemovalMethod.EmptyRecycleBin, false, P("C:/$Recycle.Bin/{Sid}"), None, None, None, null },
        { "crash-dumps", SafetyLabel.Safe, RemovalMethod.Quarantine, false,
            P("{LocalAppData}/CrashDumps", "{LocalAppData}/Microsoft/Windows/WER"), None, None, None, null },
        { "crash-dumps-system", SafetyLabel.Safe, RemovalMethod.Quarantine, true,
            P("{Windows}/Minidump", "{Windows}/MEMORY.DMP", "C:/ProgramData/Microsoft/Windows/WER"), None, None, None, null },
    };

    [Theory]
    [MemberData(nameof(Expected))]
    public void ShippedRules_ExactContent(string id, SafetyLabel label, RemovalMethod method, bool admin,
        string[] paths, string[] folders, string[] siblings, string[] exts, int? minAge)
    {
        var r = Get(id);
        Assert.Equal(label, r.Label);
        Assert.Equal(method, r.Method);
        Assert.Equal(admin, r.NeedsAdmin);
        Assert.Equal(paths, r.Paths);
        Assert.Equal(folders, r.FolderNames);
        Assert.Equal(siblings, r.RequireSibling);
        Assert.Equal(exts, r.Extensions);
        Assert.Equal(minAge, r.MinAgeDays);
        Assert.Equal(method == RemovalMethod.Quarantine, r.Undoable);
    }

    [Fact]
    public void ShippedRules_TableCoversEveryRule() =>
        Assert.Equal(Expected.Select(d => (string)d[0]!).Order(), RuleLoader.LoadAll(DataDir).Select(r => r.Id).Order());

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
    [InlineData("dotnet-build", "Comes back on the next install or build")]
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
