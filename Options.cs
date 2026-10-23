namespace Fq;

enum When { Auto, Always, Never }

/// <summary>Command-line misuse. Exit status 2.</summary>
sealed class UsageException(string message) : Exception(message);

/// <summary>A problem that makes the search impossible, e.g. a bad regex. Exit status 1.</summary>
sealed class FqException(string message) : Exception(message);

sealed class Options
{
    public string Pattern = "";
    public List<string> AndPatterns = [];
    public List<string> Paths = [];
    public bool SearchPathGiven;

    public bool Glob, FixedStrings, Exact, FullPath;
    public bool? CaseSensitive;

    public List<string> Extensions = [];
    public TypeFilter? Types;
    public List<SizeFilter> Sizes = [];
    public TimeFilter? ChangedWithin, ChangedBefore;
    public int MaxDepth = int.MaxValue, MinDepth;
    public List<string> Excludes = [], IgnoreFiles = [], IgnoreContain = [];

    public bool Hidden, NoSkip;
    public bool NoIgnore, NoIgnoreVcs, NoRequireGit, NoIgnoreParent, NoGlobalIgnoreFile;
    public bool Follow, OneFileSystem, Prune;

    public bool AbsolutePath, Print0, ListDetails, Quiet, ShowErrors;
    public string? Format, PathSeparator, BaseDirectory;
    public When Color = When.Auto, Hyperlink = When.Never, StripCwdPrefix = When.Auto;

    public int Threads = Math.Min(Environment.ProcessorCount, 64);
    public int MaxResults;

    public List<List<string>> Commands = [];
    public bool ExecBatch;
    public int BatchSize;

    public const string Version = "fq 1.0 (compatible with fd 10.5)";

    public const string Usage = """
        Usage: fq [OPTIONS] [pattern] [path]...

        Arguments:
          [pattern]                    regular expression (glob with -g) matched against file names; default: everything
          [path]...                    root directories to search; default: current directory

        Matching:
          -s, --case-sensitive         case-sensitive (default: smart case)
          -i, --ignore-case            case-insensitive
          -g, --glob                   glob pattern instead of regex
              --regex                  regex pattern (default)
          -F, --fixed-strings          literal substring
              --exact                  whole name must equal the pattern
              --and <pattern>          additional pattern that must also match (repeatable)
          -p, --full-path              match against the absolute path instead of the name

        Filters:
          -t, --type <type>            file (f), directory (d), symlink (l), executable (x), empty (e) (repeatable)
          -e, --extension <ext>        file extension (repeatable)
          -d, --max-depth <n>          maximum depth        --min-depth <n>   --exact-depth <n>
          -S, --size <+|-><n><unit>    size limit: b, k, m, g, t (decimal), ki, mi, gi, ti (binary)
              --changed-within <t>     modified after a duration (2d, 10h) or date (2024-05-01, '2024-05-01 10:00:00')
              --changed-before <t>     modified before a duration or date
          -E, --exclude <glob>         exclude entries matching the glob (repeatable)
              --prune                  don't descend into matching directories
              --ignore-contain <name>  skip directories that contain this entry

        Hidden and ignored entries:
          -H, --hidden                 include hidden entries (dot-names, Hidden attribute)
          -I, --no-ignore              don't read .gitignore, .ignore, .fdignore
          -u, --unrestricted           -H -I --no-skip
              --no-ignore-vcs          don't read .gitignore
              --no-require-git         read .gitignore outside git repositories
              --no-ignore-parent       don't read ignore files of parent directories
              --ignore-file <path>     additional ignore file (repeatable)
              --no-skip                also search node_modules, .git, Windows, ... (skipped by default)
          -L, --follow                 follow symlinks and junctions
              --one-file-system        don't cross volume boundaries

        Output:
          -a, --absolute-path          print absolute paths
          -0, --print0                 separate results with NUL
          -l, --list-details           long listing through GNU ls
              --format <fmt>           print using a template: {} {/} {//} {.} {/.}
          -c, --color <when>           auto, always, never
              --hyperlink[=<when>]     terminal hyperlinks
              --path-separator <sep>   path separator to print
              --strip-cwd-prefix[=<when>]
          -x, --exec <cmd>... [;]      run a command for each result, in parallel
          -X, --exec-batch <cmd>... [;] run one command with all results   --batch-size <n>

        Control:
          -j, --threads <n>            number of threads
              --max-results <n>        stop after n results             -1  stop after the first
          -q, --quiet                  print nothing; exit status 0 if there is a result, else 1
              --show-errors            report directories that cannot be read
          -C, --base-directory <path>  change the working directory
              --search-path <path>     search path as an option (repeatable)
          -h, --help    -V, --version
        """;
}
