namespace Fd;

[Flags]
enum IgnoreFileKinds { None = 0, FdIgnore = 1, Ignore = 2, GitIgnore = 4, Git = 8, GitDirectory = 16 }

/// <summary>The ignore files read from one directory. Directories without any share their parent's level.</summary>
sealed class IgnoreLevel
{
    public required IgnoreLevel? Parent { get; init; }
    public required string Directory { get; init; }
    public required int PrefixLength { get; init; }
    public bool HasGit { get; init; }
    public bool AnyGit { get; init; }
    public bool HasWhitelist { get; init; }
    public Gitignore? Custom { get; init; }
    public Gitignore? Ignore { get; init; }
    public Gitignore? Git { get; init; }
    public Gitignore? Exclude { get; init; }
    internal bool GitAtOrAbove { get; init; }
}

/// <summary>
/// The ignore semantics of fd (the Rust <c>ignore</c> crate): .fdignore beats .ignore beats .gitignore beats .git/info/exclude
/// beats the global git ignore beats --ignore-file; within one kind the nearest directory with a matching rule decides.
/// With git required, .gitignore files only count inside a repository and not above its root.
/// </summary>
sealed class IgnoreRules
{
    readonly bool readIgnore, readVcs, requireGit, readParents;
    readonly Gitignore? gitGlobal;
    readonly Gitignore[] explicitFiles;
    readonly bool sharedWhitelist;

    public IgnoreRules(Options options)
    {
        readIgnore = !options.NoIgnore;
        readVcs = !options.NoIgnore && !options.NoIgnoreVcs;
        requireGit = !options.NoRequireGit;
        readParents = !options.NoIgnoreParent && (readIgnore || readVcs);
        if (readVcs) gitGlobal = Gitignore.Load(GitGlobalIgnorePath());

        var files = new List<Gitignore>();
        if (readIgnore && !options.NoGlobalIgnoreFile && Gitignore.Load(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fd", "ignore")) is { } fdGlobal)
            files.Add(fdGlobal);
        foreach (string path in options.IgnoreFiles)
        {
            if (Gitignore.Load(path) is { } file) files.Add(file);
            else if (!System.IO.File.Exists(path)) Console.Error.WriteLine($"[fd error]: Malformed pattern in custom ignore file. {path}: file not found.");
        }
        explicitFiles = [.. files];

        sharedWhitelist = (gitGlobal?.HasWhitelist ?? false) || explicitFiles.Any(f => f.HasWhitelist);
    }

    public bool Enabled => readIgnore || readVcs || explicitFiles.Length > 0;

    /// <summary>True if a rule anywhere could un-hide a hidden entry, i.e. hidden entries cannot be dropped early.</summary>
    public bool MayWhitelist(IgnoreLevel? level) => sharedWhitelist || (level?.HasWhitelist ?? false);

    public static IgnoreFileKinds Classify(ReadOnlySpan<char> name, FileAttributes attributes)
    {
        if (name.Length < 4 || name[0] != '.') return IgnoreFileKinds.None;
        bool directory = (attributes & FileAttributes.Directory) != 0;
        return name.Length switch
        {
            4 when name.Equals(".git", StringComparison.OrdinalIgnoreCase) => IgnoreFileKinds.Git | (directory ? IgnoreFileKinds.GitDirectory : 0),
            7 when !directory && name.Equals(".ignore", StringComparison.OrdinalIgnoreCase) => IgnoreFileKinds.Ignore,
            9 when !directory && name.Equals(".fdignore", StringComparison.OrdinalIgnoreCase) => IgnoreFileKinds.FdIgnore,
            10 when !directory && name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase) => IgnoreFileKinds.GitIgnore,
            _ => IgnoreFileKinds.None,
        };
    }

    /// <summary>
    /// The levels of the directories above a search root, outermost first. Without parent ignore files their rules are not
    /// read, but a repository above the root still makes the root's own .gitignore count.
    /// </summary>
    public IgnoreLevel? LoadParents(string root)
    {
        if (!readParents && !readVcs) return null;

        var ancestors = new List<string>();
        for (string? directory = Path.GetDirectoryName(root); directory is not null; directory = Path.GetDirectoryName(directory))
            ancestors.Add(directory);

        IgnoreLevel? level = null;
        for (int i = ancestors.Count - 1; i >= 0; i--)
        {
            IgnoreFileKinds found = IgnoreFileKinds.None;
            foreach (string name in new[] { ".fdignore", ".ignore", ".gitignore", ".git" })
            {
                string path = Path.Join(ancestors[i], name);
                if (!Path.Exists(path)) continue;
                found |= Classify(name, Directory.Exists(path) ? FileAttributes.Directory : 0);
            }
            level = Load(level, ancestors[i], readParents ? found : found & IgnoreFileKinds.Git);
        }
        return level;
    }

    /// <summary>Reads the ignore files that <paramref name="found"/> says exist. Returns <paramref name="parent"/> if there is nothing to add.</summary>
    public IgnoreLevel? Load(IgnoreLevel? parent, string directory, IgnoreFileKinds found)
    {
        bool hasGit = requireGit && readVcs && (found & IgnoreFileKinds.Git) != 0;
        bool gitAtOrAbove = hasGit || (parent?.GitAtOrAbove ?? false);
        bool consultGit = !requireGit || gitAtOrAbove;

        Gitignore? custom = readIgnore && (found & IgnoreFileKinds.FdIgnore) != 0 ? Gitignore.Load(Path.Join(directory, ".fdignore")) : null;
        Gitignore? ignore = readIgnore && (found & IgnoreFileKinds.Ignore) != 0 ? Gitignore.Load(Path.Join(directory, ".ignore")) : null;
        Gitignore? git = readVcs && consultGit && (found & IgnoreFileKinds.GitIgnore) != 0 ? Gitignore.Load(Path.Join(directory, ".gitignore")) : null;
        Gitignore? exclude = readVcs && consultGit && (found & IgnoreFileKinds.GitDirectory) != 0 ? Gitignore.Load(Path.Join(directory, ".git", "info", "exclude")) : null;

        if (custom is null && ignore is null && git is null && exclude is null && !hasGit) return parent;

        return new IgnoreLevel
        {
            Parent = parent,
            Directory = directory,
            PrefixLength = directory.Length + (directory.EndsWith('\\') ? 0 : 1),
            HasGit = hasGit,
            GitAtOrAbove = gitAtOrAbove,
            AnyGit = consultGit,
            HasWhitelist = (parent?.HasWhitelist ?? false) || (custom?.HasWhitelist ?? false) || (ignore?.HasWhitelist ?? false)
                || (git?.HasWhitelist ?? false) || (exclude?.HasWhitelist ?? false),
            Custom = custom,
            Ignore = ignore,
            Git = git,
            Exclude = exclude,
        };
    }

    /// <param name="directory">The entry's full directory path.</param>
    /// <param name="rootRelative">The same directory relative to the search root, for the rules that belong to no directory.</param>
    public IgnoreMatch Match(IgnoreLevel? level, ReadOnlySpan<char> directory, ReadOnlySpan<char> rootRelative, ReadOnlySpan<char> name, bool isDirectory)
    {
        IgnoreMatch custom = IgnoreMatch.None, ignore = IgnoreMatch.None, git = IgnoreMatch.None, exclude = IgnoreMatch.None;
        bool anyGit = level?.AnyGit ?? !requireGit;
        bool sawGit = false;

        for (IgnoreLevel? l = level; l is not null; l = l.Parent)
        {
            ReadOnlySpan<char> relative = directory.Length > l.Directory.Length ? directory[l.PrefixLength..] : default;
            if (custom == IgnoreMatch.None && l.Custom is { } c) custom = c.Match(relative, name, isDirectory);
            if (ignore == IgnoreMatch.None && l.Ignore is { } i) ignore = i.Match(relative, name, isDirectory);
            if (anyGit && !sawGit)
            {
                if (git == IgnoreMatch.None && l.Git is { } g) git = g.Match(relative, name, isDirectory);
                if (exclude == IgnoreMatch.None && l.Exclude is { } x) exclude = x.Match(relative, name, isDirectory);
            }
            sawGit |= l.HasGit;
        }

        IgnoreMatch result = custom != IgnoreMatch.None ? custom : ignore != IgnoreMatch.None ? ignore : git != IgnoreMatch.None ? git : exclude;
        if (result != IgnoreMatch.None) return result;

        if (anyGit && gitGlobal is { } global && (result = global.Match(rootRelative, name, isDirectory)) != IgnoreMatch.None) return result;
        for (int k = explicitFiles.Length - 1; k >= 0; k--)
        {
            if ((result = explicitFiles[k].Match(rootRelative, name, isDirectory)) != IgnoreMatch.None) return result;
        }
        return IgnoreMatch.None;
    }

    /// <summary>The file named by core.excludesFile in the global git config, or git's default location.</summary>
    static string GitGlobalIgnorePath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Join(home, ".config");

        foreach (string config in new[] { Path.Join(home, ".gitconfig"), Path.Join(configHome, "git", "config") })
        {
            if (ReadExcludesFile(config) is { } path) return path.StartsWith('~') ? home + path[1..] : path;
        }
        return Path.Join(configHome, "git", "ignore");
    }

    static string? ReadExcludesFile(string config)
    {
        if (!System.IO.File.Exists(config)) return null;
        bool inCore = false;
        foreach (string raw in System.IO.File.ReadLines(config))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inCore = line.StartsWith("[core]", StringComparison.OrdinalIgnoreCase);
            }
            else if (inCore && line.StartsWith("excludesfile", StringComparison.OrdinalIgnoreCase) && line.IndexOf('=') is > 0 and var equals)
            {
                return line[(equals + 1)..].Trim().Trim('"');
            }
        }
        return null;
    }
}
