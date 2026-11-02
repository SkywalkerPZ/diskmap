using DiskMap.Core.Rules;
using DiskMap.Core.Safety;
using DiskMap.Core.Scanning;

namespace DiskMap.Core.Tests;

public class RuleMatcherTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Rules", "data");
    private static readonly IReadOnlyList<Rule> Rules = RuleLoader.LoadAll(DataDir);
    private static readonly DateTime Now = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private static string Win(Fixture fx) => Path.Combine(fx.Root, "Windows");

    private static async Task<IReadOnlyList<RuleMatch>> Match(Fixture fx, UserContext? user = null)
    {
        var r = await new FolderWalkScanner().ScanAsync(fx.Root, null, CancellationToken.None);
        return RuleMatcher.MatchAll(r.Root, Rules, user ?? fx.User, Win(fx), Now);
    }

    [Fact]
    public async Task NodeModules_UnderProfile_Matches()
    {
        using var fx = new Fixture();
        var nm = Path.Combine("Users", "tester", "proj", "node_modules");
        fx.File(Path.Combine(nm, "a.js"), 10);
        var m = Assert.Single(await Match(fx), x => x.Rule.Id == "dev-caches");
        Assert.Equal(Path.Combine(fx.Root, nm), m.Node.FullPath);
    }

    [Fact]
    public async Task NodeModules_InOtherProfile_DoesNotMatch()
    {
        using var fx = new Fixture();
        fx.File(Path.Combine("Users", "other", "proj", "node_modules", "a.js"), 10);
        Assert.DoesNotContain(await Match(fx), x => x.Rule.Id == "dev-caches");
    }

    [Fact]
    public async Task Bin_WithoutCsproj_DoesNotMatch()
    {
        using var fx = new Fixture();
        fx.File(Path.Combine("Users", "tester", "p", "bin", "a.dll"), 10);
        Assert.DoesNotContain(await Match(fx), x => x.Rule.Id == "dotnet-build");
    }

    [Fact]
    public async Task Bin_WithCsproj_Matches()
    {
        using var fx = new Fixture();
        fx.File(Path.Combine("Users", "tester", "p", "bin", "a.dll"), 10);
        fx.File(Path.Combine("Users", "tester", "p", "p.csproj"), 10);
        var m = Assert.Single(await Match(fx), x => x.Rule.Id == "dotnet-build");
        Assert.Equal("bin", m.Node.Name);
    }

    [Fact]
    public async Task OldInstaller_89Days_NoMatch_91Days_Match()
    {
        using var fx = new Fixture();
        fx.File(Path.Combine("Users", "tester", "Downloads", "new.exe"), 10, Now.AddDays(-89));
        fx.File(Path.Combine("Users", "tester", "Downloads", "setup.exe"), 10, Now.AddDays(-91));
        fx.File(Path.Combine("Users", "tester", "Downloads", "old.txt"), 10, Now.AddDays(-400));
        var names = (await Match(fx)).Where(x => x.Rule.Id == "old-installers").Select(x => x.Node.Name).ToList();
        Assert.Equal(["setup.exe"], names);
    }

    [Fact]
    public async Task NestedMatch_ReportsOutermostOnly()
    {
        using var fx = new Fixture();
        fx.File(Path.Combine("Users", "tester", "p", "node_modules", "x", "node_modules", "a.js"), 10);
        Assert.Single(await Match(fx), x => x.Rule.Id == "dev-caches");
    }

    [Fact]
    public async Task Wildcard_MatchesExactlyOneSegment()
    {
        using var fx = new Fixture();
        var ud = Path.Combine("Users", "tester", "AppData", "Local", "Google", "Chrome", "User Data");
        fx.File(Path.Combine(ud, "Default", "Cache", "a"), 10);
        fx.File(Path.Combine(ud, "Cache", "b"), 10);                 // no profile segment: must not match
        fx.File(Path.Combine(ud, "Default", "x", "Cache", "c"), 10); // two segments: must not match
        var m = Assert.Single(await Match(fx), x => x.Rule.Id == "browser-cache");
        Assert.Equal(Path.Combine(fx.Root, ud, "Default", "Cache"), m.Node.FullPath);
    }

    [Fact]
    public async Task TempEqualToProfile_NeverMatchesProfile()
    {
        using var fx = new Fixture();
        fx.File(Path.Combine("Users", "tester", "doc.txt"), 10);
        var u = fx.User with { Temp = fx.User.ProfilePath };
        Assert.DoesNotContain(await Match(fx, u), x => x.Rule.Id == "temp");
        Assert.False(SafetyRails.CanRemove(u.ProfilePath, Rules.Single(r => r.Id == "temp"), u, Win(fx), out _));
    }
}
