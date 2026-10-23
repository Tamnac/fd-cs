using System.Globalization;

namespace Fq;

/// <summary>Argument parsing with the conventions of fd's clap definition: combined short flags, --opt=value, --, overriding repeats.</summary>
static class CommandLine
{
    static readonly Dictionary<string, string> LongAliases = new()
    {
        ["maxdepth"] = "max-depth", ["mindepth"] = "min-depth", ["literal"] = "fixed-strings",
        ["dereference"] = "follow", ["has-results"] = "quiet", ["mount"] = "one-file-system", ["xdev"] = "one-file-system",
        ["change-newer-than"] = "changed-within", ["newer"] = "changed-within", ["changed-after"] = "changed-within",
        ["change-older-than"] = "changed-before", ["older"] = "changed-before", ["hyper"] = "hyperlink",
    };

    static readonly Dictionary<char, string> ShortNames = new()
    {
        ['H'] = "hidden", ['I'] = "no-ignore", ['u'] = "unrestricted", ['s'] = "case-sensitive", ['i'] = "ignore-case",
        ['g'] = "glob", ['F'] = "fixed-strings", ['a'] = "absolute-path", ['l'] = "list-details", ['L'] = "follow",
        ['p'] = "full-path", ['0'] = "print0", ['1'] = "max-one", ['q'] = "quiet", ['h'] = "help", ['V'] = "version",
        ['d'] = "max-depth", ['E'] = "exclude", ['t'] = "type", ['e'] = "extension", ['S'] = "size", ['c'] = "color",
        ['j'] = "threads", ['C'] = "base-directory", ['x'] = "exec", ['X'] = "exec-batch",
    };

    static readonly HashSet<string> ValueOptions =
    [
        "max-depth", "min-depth", "exact-depth", "exclude", "type", "extension", "size", "changed-within", "changed-before",
        "format", "batch-size", "ignore-file", "color", "threads", "max-results", "base-directory", "path-separator",
        "search-path", "and", "ignore-contain", "max-buffer-time",
    ];

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var positional = new List<string>();
        var set = new HashSet<string>();
        bool onlyPositional = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (onlyPositional || arg.Length < 2 || arg[0] != '-') { positional.Add(arg); continue; }
            if (arg == "--") { onlyPositional = true; continue; }

            if (arg[1] == '-')
            {
                string name = arg[2..];
                string? inline = null;
                int equals = name.IndexOf('=');
                if (equals >= 0) (name, inline) = (name[..equals], name[(equals + 1)..]);
                i = Apply(o, set, LongAliases.GetValueOrDefault(name, name), inline, args, i, "--" + name);
                continue;
            }

            for (int c = 1; c < arg.Length; c++)
            {
                if (!ShortNames.TryGetValue(arg[c], out string? name)) throw new UsageException($"unexpected argument '-{arg[c]}' found");
                if (!TakesValue(name))
                {
                    i = Apply(o, set, name, null, args, i, "-" + arg[c]);
                    continue;
                }
                string rest = arg[(c + 1)..];
                if (rest.StartsWith('=')) rest = rest[1..];
                i = Apply(o, set, name, rest.Length > 0 ? rest : null, args, i, "-" + arg[c]);
                break;
            }
        }

        if (positional.Count > 0) o.Pattern = positional[0];
        if (positional.Count > 1 && set.Contains("search-path")) throw new UsageException("the argument '--search-path' cannot be used with '[path]...'");
        o.Paths.AddRange(positional.Skip(1));
        Validate(o, set);
        return o;
    }

    static bool TakesValue(string name) => ValueOptions.Contains(name) || name is "exec" or "exec-batch";

    static int Apply(Options o, HashSet<string> set, string name, string? inline, string[] args, int i, string shown)
    {
        set.Add(name);

        if (name is "hyperlink" or "strip-cwd-prefix")
        {
            When when = inline is null ? (name == "hyperlink" ? When.Auto : When.Always) : ParseWhen(inline, shown);
            if (name == "hyperlink") o.Hyperlink = when; else o.StripCwdPrefix = when;
            return i;
        }

        if (name is "exec" or "exec-batch")
        {
            var command = new List<string>();
            if (inline is not null) command.Add(inline);
            while (i + 1 < args.Length && args[++i] != ";") command.Add(args[i]);
            if (command.Count == 0) throw new UsageException($"a value is required for '{shown} <cmd>...' but none was supplied");
            o.Commands.Add(command);
            o.ExecBatch = name == "exec-batch";
            return i;
        }

        if (!ValueOptions.Contains(name))
        {
            if (inline is not null) throw new UsageException($"unexpected value '{inline}' for '{shown}': this option does not take a value");
            ApplyFlag(o, name);
            return i;
        }

        string value = inline ?? (i + 1 < args.Length ? args[++i] : throw new UsageException($"a value is required for '{shown} <{name}>' but none was supplied"));
        ApplyValue(o, name, value, shown);
        return i;
    }

    static void ApplyFlag(Options o, string name)
    {
        switch (name)
        {
            case "hidden": o.Hidden = true; break;
            case "no-hidden": o.Hidden = false; break;
            case "no-ignore": o.NoIgnore = true; break;
            case "ignore": o.NoIgnore = false; break;
            case "no-ignore-vcs": o.NoIgnoreVcs = true; break;
            case "ignore-vcs": o.NoIgnoreVcs = false; break;
            case "no-require-git": o.NoRequireGit = true; break;
            case "require-git": o.NoRequireGit = false; break;
            case "no-ignore-parent": o.NoIgnoreParent = true; break;
            case "ignore-parent": o.NoIgnoreParent = false; break;
            case "no-global-ignore-file": o.NoGlobalIgnoreFile = true; break;
            case "unrestricted": o.Hidden = o.NoIgnore = o.NoSkip = true; break;
            case "no-skip": o.NoSkip = true; break;
            case "case-sensitive": o.CaseSensitive = true; break;
            case "ignore-case": o.CaseSensitive = false; break;
            case "glob": o.Glob = true; break;
            case "regex": o.Glob = false; break;
            case "fixed-strings": o.FixedStrings = true; break;
            case "exact": o.Exact = true; break;
            case "absolute-path": o.AbsolutePath = true; break;
            case "relative-path": o.AbsolutePath = false; break;
            case "list-details": o.ListDetails = true; break;
            case "follow": o.Follow = true; break;
            case "no-follow": o.Follow = false; break;
            case "full-path": o.FullPath = true; break;
            case "print0": o.Print0 = true; break;
            case "prune": o.Prune = true; break;
            case "max-one": o.MaxResults = 1; break;
            case "quiet": o.Quiet = true; break;
            case "show-errors": o.ShowErrors = true; break;
            case "one-file-system": o.OneFileSystem = true; break;
            case "help": throw new HelpRequested(Options.Usage);
            case "version": throw new HelpRequested(Options.Version);
            default: throw new UsageException($"unexpected argument '--{name}' found");
        }
    }

    static void ApplyValue(Options o, string name, string value, string shown)
    {
        switch (name)
        {
            case "max-depth": o.MaxDepth = ParseCount(value, shown, allowZero: true); break;
            case "min-depth": o.MinDepth = ParseCount(value, shown, allowZero: true); break;
            case "exact-depth": o.MaxDepth = o.MinDepth = ParseCount(value, shown, allowZero: true); break;
            case "exclude": o.Excludes.Add(value); break;
            case "type": AddType(o, value, shown); break;
            case "extension": o.Extensions.Add(value.TrimStart('.')); break;
            case "size": o.Sizes.Add(SizeFilter.Parse(value)); break;
            case "changed-within": o.ChangedWithin = TimeFilter.Parse(value); break;
            case "changed-before": o.ChangedBefore = TimeFilter.Parse(value); break;
            case "format": o.Format = value; break;
            case "batch-size": o.BatchSize = ParseCount(value, shown, allowZero: true); break;
            case "ignore-file": o.IgnoreFiles.Add(value); break;
            case "color": o.Color = ParseWhen(value, shown); break;
            case "threads": o.Threads = ParseCount(value, shown, allowZero: false); break;
            case "max-results": o.MaxResults = ParseCount(value, shown, allowZero: true); break;
            case "base-directory": o.BaseDirectory = value; break;
            case "path-separator": o.PathSeparator = value; break;
            case "search-path": o.Paths.Add(value); o.SearchPathGiven = true; break;
            case "and": o.AndPatterns.Add(value); break;
            case "ignore-contain": o.IgnoreContain.Add(value); break;
        }
    }

    static int ParseCount(string text, string shown, bool allowZero)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || (value == 0 && !allowZero))
            throw new UsageException($"invalid value '{text}' for '{shown} <num>': {(text.Length > 0 && text.All(char.IsAsciiDigit) ? "number would be zero for non-zero type" : "invalid digit found in string")}");
        return value;
    }

    static When ParseWhen(string text, string shown) => text switch
    {
        "auto" => When.Auto,
        "always" => When.Always,
        "never" => When.Never,
        _ => throw new UsageException($"invalid value '{text}' for '{shown} <when>': [possible values: auto, always, never]"),
    };

    static void AddType(Options o, string value, string shown)
    {
        var types = o.Types ??= new TypeFilter();
        switch (value)
        {
            case "f" or "file": types.Files = true; break;
            case "d" or "dir" or "directory": types.Directories = true; break;
            case "l" or "symlink": types.Symlinks = true; break;
            case "x" or "executable": types.ExecutablesOnly = types.Files = true; break;
            case "e" or "empty": types.EmptyOnly = true; break;
            case "s" or "socket" or "p" or "pipe" or "b" or "block-device" or "c" or "char-device": break;
            default:
                throw new UsageException($"invalid value '{value}' for '{shown} <filetype>'\n  [possible values: file, directory, symlink, block-device, char-device, executable, empty, socket, pipe]");
        }
    }

    static void Validate(Options o, HashSet<string> set)
    {
        void Conflict(string a, string b)
        {
            if (set.Contains(a) && set.Contains(b)) throw new UsageException($"the argument '--{a}' cannot be used with '--{b}'");
        }

        Conflict("glob", "fixed-strings");
        Conflict("exact", "glob");
        Conflict("exact", "fixed-strings");
        Conflict("list-details", "absolute-path");
        Conflict("list-details", "print0");
        Conflict("list-details", "format");
        Conflict("exact-depth", "max-depth");
        Conflict("exact-depth", "min-depth");
        Conflict("prune", "size");
        Conflict("prune", "exact-depth");
        Conflict("quiet", "max-results");
        Conflict("quiet", "max-one");

        string? command = set.Contains("exec") ? "exec" : set.Contains("exec-batch") ? "exec-batch" : set.Contains("list-details") ? "list-details" : null;
        if (command is not null)
        {
            foreach (string other in new[] { "max-results", "quiet", "max-one" })
                if (set.Contains(other)) throw new UsageException($"the argument '--{command}' cannot be used with '--{other}'");
        }
        if (set.Contains("exec") && set.Contains("exec-batch")) throw new UsageException("the argument '--exec' cannot be used with '--exec-batch'");
        if (set.Contains("batch-size") && !set.Contains("exec-batch")) throw new UsageException("the following required arguments were not provided: --exec-batch");
        if (set.Contains("strip-cwd-prefix") && o.Paths.Count > 0) throw new UsageException("the argument '--strip-cwd-prefix' cannot be used with '[path]...'");
    }
}

/// <summary>Thrown for --help and --version; the message goes to stdout.</summary>
sealed class HelpRequested(string text) : Exception(text);
