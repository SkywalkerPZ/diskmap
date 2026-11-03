namespace DiskMap.Core.Rules;

public enum SafetyLabel { Safe, SafeIfNoHibernate, Rebuildable, CheckFirst, AlreadyDeleted }

public enum RemovalMethod { Quarantine, DismComponentCleanup, PowercfgHibernateOff, EmptyRecycleBin }

/// <summary>A recognition rule: the only thing that can make an item removable.</summary>
public sealed record Rule
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required SafetyLabel Label { get; init; }
    public required string Explanation { get; init; }
    public required string Consequence { get; init; }
    public required RemovalMethod Method { get; init; }
    public required bool NeedsAdmin { get; init; }
    /// <summary>Path templates; <c>*</c> matches exactly one path segment (matched in Task 5).</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];
    /// <summary>Any folder with one of these names under {UserProfile}.</summary>
    public IReadOnlyList<string> FolderNames { get; init; } = [];
    /// <summary>Glob patterns; a folder-name match also needs a sibling matching one of them.</summary>
    public IReadOnlyList<string> RequireSibling { get; init; } = [];
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public int? MinAgeDays { get; init; }

    public bool Undoable => Method == RemovalMethod.Quarantine;

    /// <summary>Resolves inside the launching user's own folders (or their Recycle Bin), which that user can write to.</summary>
    public bool IsPerUser => Method == RemovalMethod.EmptyRecycleBin || FolderNames.Count > 0
        || Paths.Any(p => UserTokens.Any(t => p.Contains(t, StringComparison.OrdinalIgnoreCase)));

    private static readonly string[] UserTokens = ["{UserProfile}", "{LocalAppData}", "{AppData}", "{Temp}", "{Downloads}", "{Sid}"];
}
