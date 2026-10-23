namespace Fq;

/// <summary>Decides, from raw directory record fields, whether to visit or report an entry. Allocation-free.</summary>
sealed class Filter
{
    static readonly string[] SkippedDirectories =
    [
        "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "Recovery", "Intel", "AMD", "NVIDIA",
        "node_modules", ".git", ".svn", "__pycache__", "obj", "bin", "Debug",
        "Release", ".vs", "packages", "bower_components", "dist", "build",
    ];

    readonly Options options;
    readonly NameMatcher? name;
    readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> skipped;
    readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> extensions;
    readonly bool checkSize, checkTime;

    public Filter(Options options)
    {
        this.options = options;
        name = NameMatcher.Create(options);
        var skipSet = new HashSet<string>(options.NoSkip ? [] : SkippedDirectories, StringComparer.OrdinalIgnoreCase);
        skipped = skipSet.GetAlternateLookup<ReadOnlySpan<char>>();
        extensions = options.Extensions.GetAlternateLookup<ReadOnlySpan<char>>();
        checkSize = options.MinSize > 0 || options.MaxSize < long.MaxValue;
        checkTime = options.After is not null || options.Before is not null;
    }

    public bool ShouldTraverse(ReadOnlySpan<char> directoryName, FileAttributes attributes)
    {
        if (!options.FollowSymlinks && (attributes & FileAttributes.ReparsePoint) != 0) return false;
        return !IsHiddenName(directoryName) && !skipped.Contains(directoryName);
    }

    public bool MatchesDirectory(ReadOnlySpan<char> directoryName, long lastWriteTime) =>
        (name is null || name.IsMatch(directoryName)) && TimeMatches(lastWriteTime);

    public bool MatchesFile(ReadOnlySpan<char> fileName, long length, long lastWriteTime)
    {
        if (IsHiddenName(fileName)) return false;
        if (name is not null && !name.IsMatch(fileName)) return false;
        if (options.Extensions.Count > 0)
        {
            ReadOnlySpan<char> ext = Path.GetExtension(fileName);
            if (ext.IsEmpty || !extensions.Contains(ext[1..])) return false;
        }
        if (checkSize && (length < options.MinSize || length > options.MaxSize)) return false;
        return TimeMatches(lastWriteTime);
    }

    bool IsHiddenName(ReadOnlySpan<char> entryName) => !options.IncludeHidden && entryName[0] == '.';

    bool TimeMatches(long lastWriteTime)
    {
        if (!checkTime) return true;
        DateTime modified = DateTime.FromFileTimeUtc(lastWriteTime);
        return (options.After is not { } after || modified >= after)
            && (options.Before is not { } before || modified < before);
    }
}
