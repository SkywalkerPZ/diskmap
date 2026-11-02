using System.IO.Enumeration;
using DiskMap.Core.Safety;
using DiskMap.Core.Scanning;

namespace DiskMap.Core.Rules;

public sealed record RuleMatch(Rule Rule, FileNode Node);

/// <summary>Finds the nodes a rule recognises. Anything doubtful is not matched; SafetyRails.CanRemove is the final gate for every match.</summary>
public static class RuleMatcher
{
    private sealed record Compiled(Rule Rule, string[][] Anchors, HashSet<string> Names, HashSet<string> Exts, bool FileRule);

    public static IReadOnlyList<RuleMatch> MatchAll(FileNode root, IReadOnlyList<Rule> rules, UserContext user, string windowsDir, DateTime nowUtc)
    {
        var compiled = Compile(rules, user, windowsDir);
        var found = new List<RuleMatch>();
        var stack = new Stack<(FileNode Node, string Path)>();
        stack.Push((root, root.Name));
        while (stack.Count > 0)
        {
            var (node, path) = stack.Pop();
            if (node.IsReparsePoint) continue; // never look through a junction or symlink
            if (TryMatch(node, path, compiled, user, windowsDir, nowUtc) is { } m) { found.Add(m); continue; } // outermost only
            foreach (var c in node.Children) stack.Push((c, Path.Join(path, c.Name)));
        }
        return found;
    }

    public static RuleMatch? MatchNode(FileNode node, IReadOnlyList<Rule> rules, UserContext user, string windowsDir, DateTime nowUtc) =>
        node.IsReparsePoint ? null : TryMatch(node, node.FullPath, Compile(rules, user, windowsDir), user, windowsDir, nowUtc);

    private static Compiled[] Compile(IReadOnlyList<Rule> rules, UserContext user, string windowsDir) =>
        rules.Select(r => new Compiled(r,
            r.Paths.Select(t => SafetyRails.Segs(user.Expand(t, windowsDir))).ToArray(),
            new HashSet<string>(r.FolderNames, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(r.Extensions, StringComparer.OrdinalIgnoreCase),
            r.Extensions.Count > 0 || r.MinAgeDays is not null)).ToArray();

    private static RuleMatch? TryMatch(FileNode node, string path, Compiled[] compiled, UserContext user, string windowsDir, DateTime nowUtc)
    {
        string[]? segs = null;
        foreach (var c in compiled)
        {
            var hit = false;
            if (c.FileRule)
            {
                // files only, never the folder; the age is measured against the supplied clock
                if (node.IsDirectory) continue;
                if (c.Exts.Count > 0 && !c.Exts.Contains(Path.GetExtension(node.Name))) continue;
                if (c.Rule.MinAgeDays is { } d && nowUtc - node.LastWriteUtc <= TimeSpan.FromDays(d)) continue;
                segs ??= SafetyRails.Segs(path);
                hit = c.Anchors.Any(a => a.Length < segs.Length && SafetyRails.IsPrefix(a, segs));
            }
            else
            {
                if (node.IsDirectory && c.Names.Contains(node.Name) && HasSibling(node, c.Rule.RequireSibling)) hit = true;
                if (!hit)
                    foreach (var a in c.Anchors)
                    {
                        if (a[^1] != "*" && !string.Equals(a[^1], node.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        segs ??= SafetyRails.Segs(path);
                        if (a.Length == segs.Length && SafetyRails.IsPrefix(a, segs)) { hit = true; break; }
                    }
            }
            if (hit && SafetyRails.CanRemove(path, c.Rule, user, windowsDir, out _)) return new RuleMatch(c.Rule, node);
        }
        return null;
    }

    private static bool HasSibling(FileNode node, IReadOnlyList<string> globs) =>
        globs.Count == 0 || (node.Parent is { } p &&
            p.Children.Any(s => !s.IsDirectory && globs.Any(g => FileSystemName.MatchesSimpleExpression(g, s.Name, true))));
}
