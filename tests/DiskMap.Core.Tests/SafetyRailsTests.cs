using System.Diagnostics;
using DiskMap.Core.Rules;
using DiskMap.Core.Safety;

namespace DiskMap.Core.Tests;

public class SafetyRailsTests
{
    private static readonly IReadOnlyList<Rule> Rules = RuleLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "Rules", "data"));
    private static Rule R(string id) => Rules.Single(r => r.Id == id);
    private static readonly string Sep = Path.DirectorySeparatorChar.ToString();

    [Fact]
    public void Rails_BlockProgramFiles()
    {
        using var fx = new Fixture();
        var w = Path.Combine(fx.Root, "Windows");
        foreach (var rule in Rules)
        {
            Assert.False(SafetyRails.CanRemove("C:" + Sep + "Program Files" + Sep + "x", rule, fx.User, w, out var why));
            Assert.NotEmpty(why);
        }
    }

    [Fact]
    public void Rails_BlockWindowsExceptNamedRules()
    {
        using var fx = new Fixture();
        var w = Path.Combine(fx.Root, "Windows");
        Assert.False(SafetyRails.CanRemove(Path.Combine(w, "System32", "x"), R("temp"), fx.User, w, out _));
        Assert.False(SafetyRails.CanRemove(Path.Combine(w, "System32", "x"), R("windows-temp"), fx.User, w, out _));
        Assert.True(SafetyRails.CanRemove(Path.Combine(w, "Temp", "x"), R("windows-temp"), fx.User, w, out _));
        Assert.True(SafetyRails.CanRemove(Path.Combine(w, "WinSxS"), R("windows-update"), fx.User, w, out _));
        Assert.False(SafetyRails.CanRemove(w, R("windows-temp"), fx.User, w, out _));
    }

    [Fact]
    public void Rails_BlockWholeUserFolders()
    {
        using var fx = new Fixture();
        var w = Path.Combine(fx.Root, "Windows");
        var p = fx.User.ProfilePath;
        foreach (var f in new[] { p, Path.Combine(p, "Documents"), Path.Combine(p, "Desktop"), Path.Combine(p, "Pictures"),
                                  Path.Combine(p, "Videos"), Path.Combine(p, "Music"), Path.Combine(p, "Downloads"), fx.User.Downloads,
                                  Path.GetDirectoryName(p)!, Path.GetPathRoot(p)! })
            foreach (var rule in Rules)
                Assert.False(SafetyRails.CanRemove(f, rule, fx.User, w, out _), f + " / " + rule.Id);
        // files inside Downloads are allowed by the installer rule only
        Assert.True(SafetyRails.CanRemove(Path.Combine(fx.User.Downloads, "setup.exe"), R("old-installers"), fx.User, w, out _));
        Assert.False(SafetyRails.CanRemove(Path.Combine(fx.User.Downloads, "notes.docx"), R("old-installers"), fx.User, w, out _));
    }

    [Fact]
    public void Rails_BlockReparsePoint()
    {
        using var fx = new Fixture();
        var w = Path.Combine(fx.Root, "Windows");
        var target = fx.Dir("elsewhere");
        var link = Path.Combine(fx.User.Temp, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true };
        Process.Start(psi)!.WaitForExit();
        Assert.True(Directory.Exists(link));
        Assert.False(SafetyRails.CanRemove(link, R("temp"), fx.User, w, out _));
        Assert.False(SafetyRails.CanRemove(Path.Combine(link, "x"), R("temp"), fx.User, w, out _));
        Assert.True(SafetyRails.CanRemove(Path.Combine(fx.User.Temp, "plain"), R("temp"), fx.User, w, out _));
    }

    [Fact]
    public void Rails_OtherProfileAndPrefixSiblingsRefused()
    {
        using var fx = new Fixture();
        var w = Path.Combine(fx.Root, "Windows");
        var other = Path.Combine(fx.Root, "Users", "other", "p", "node_modules");
        Assert.False(SafetyRails.CanRemove(other, R("dev-caches"), fx.User, w, out _));
        Assert.False(SafetyRails.CanRemove(fx.User.Temp + "x", R("temp"), fx.User, w, out _)); // whole segments only
        Assert.False(SafetyRails.CanRemove("relative" + Sep + "x", R("temp"), fx.User, w, out _));
    }
}
