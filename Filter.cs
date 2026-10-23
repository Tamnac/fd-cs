namespace Fq;

[Flags]
enum EntryKinds { File = 1, Directory = 2, Link = 4, All = File | Directory | Link }

/// <summary>The --type selection. <c>x</c> and <c>e</c> narrow what <c>f</c> and <c>d</c> select.</summary>
sealed class TypeFilter
{
    public bool Files, Directories, Symlinks, ExecutablesOnly, EmptyOnly;
}

/// <summary>Decides, from raw directory record fields, whether to skip or report an entry. Allocation-free.</summary>
sealed class Filter
{
    static readonly string[] SkippedDirectories =
    [
        "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "Recovery", "node_modules", ".git",
        ".svn", "__pycache__", "obj", "bin", "Debug", "Release", ".vs", "packages",
        "bower_components", "dist", "build",
    ];

    readonly bool includeHidden;
    readonly bool skipsDirectories;
    readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> skipped;
    readonly List<NameMatcher> matchers;
    readonly bool fullPath;
    readonly string[] dottedExtensions;
    readonly EntryKinds kinds;
    readonly bool executablesOnly, emptyOnly;
    readonly SizeFilter[] sizes;
    readonly TimeFilter? changedWithin, changedBefore;

    public Filter(Options options)
    {
        includeHidden = options.Hidden;
        var skipSet = new HashSet<string>(options.NoSkip ? [] : SkippedDirectories, StringComparer.OrdinalIgnoreCase);
        skipsDirectories = skipSet.Count > 0;
        skipped = skipSet.GetAlternateLookup<ReadOnlySpan<char>>();

        matchers = NameMatcher.CreateAll(options);
        fullPath = options.FullPath;
        dottedExtensions = [.. options.Extensions.Select(e => "." + e)];

        if (options.Types is { } types)
        {
            bool both = types.EmptyOnly && !(types.Files || types.Directories);
            kinds = (types.Files || both ? EntryKinds.File : 0)
                | (types.Directories || both ? EntryKinds.Directory : 0)
                | (types.Symlinks ? EntryKinds.Link : 0);
            executablesOnly = types.ExecutablesOnly;
            emptyOnly = types.EmptyOnly;
        }
        else
        {
            kinds = EntryKinds.All;
        }

        sizes = [.. options.Sizes];
        changedWithin = options.ChangedWithin;
        changedBefore = options.ChangedBefore;
    }

    /// <summary>True if the report decision needs the entry's path: a full-path pattern or an emptiness check of a directory.</summary>
    public bool NeedsPath => fullPath || (emptyOnly && (kinds & EntryKinds.Directory) != 0);

    public bool IsHidden(ReadOnlySpan<char> name, FileAttributes attributes) =>
        !includeHidden && (name[0] == '.' || (attributes & FileAttributes.Hidden) != 0);

    public bool IsSkipped(ReadOnlySpan<char> directoryName) => skipsDirectories && skipped.Contains(directoryName);

    /// <summary>Every criterion that is decided by the directory record alone.</summary>
    public bool Matches(EntryKinds kind, ReadOnlySpan<char> name, long length, long lastWriteTime)
    {
        if ((kinds & kind) == 0) return false;
        if (!fullPath)
        {
            foreach (NameMatcher matcher in matchers)
            {
                if (!matcher.IsMatch(name)) return false;
            }
        }
        if (dottedExtensions.Length > 0 && !HasExtension(name)) return false;
        if (executablesOnly && !IsExecutable(name)) return false;
        if (sizes.Length > 0 && !SizeMatches(kind, length)) return false;
        if (changedWithin is { } within && !within.IsAfter(lastWriteTime)) return false;
        if (changedBefore is { } before && !before.IsBefore(lastWriteTime)) return false;
        return !emptyOnly || (kind == EntryKinds.File ? length == 0 : kind == EntryKinds.Directory);
    }

    /// <summary>The criteria that need the path, for entries that passed <see cref="Matches"/>.</summary>
    public bool MatchesPath(EntryKinds kind, ReadOnlySpan<char> path)
    {
        if (fullPath)
        {
            foreach (NameMatcher matcher in matchers)
            {
                if (!matcher.IsMatch(path)) return false;
            }
        }
        return !(emptyOnly && kind == EntryKinds.Directory) || IsEmptyDirectory(path);
    }

    bool HasExtension(ReadOnlySpan<char> name)
    {
        foreach (string extension in dottedExtensions)
        {
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    bool SizeMatches(EntryKinds kind, long length)
    {
        if (kind != EntryKinds.File) return false;
        foreach (SizeFilter size in sizes)
        {
            if (!size.IsWithin(length)) return false;
        }
        return true;
    }

    /// <summary>Windows has no executable bit: fd accepts files without an extension and the extensions exe, com, cmd and bat.</summary>
    static bool IsExecutable(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0) return true;
        return name[(dot + 1)..] is "exe" or "com" or "cmd" or "bat";
    }

    static bool IsEmptyDirectory(ReadOnlySpan<char> path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path.ToString()).GetEnumerator();
            return !entries.MoveNext();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
