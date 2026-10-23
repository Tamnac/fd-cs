using System.Globalization;

namespace Fq;

sealed class Options
{
    public string Pattern = "";
    public string Root = ".";
    public bool Glob, Regex, CaseSensitive;
    public bool Folders, FoldersOnly, FilesOnly;
    public bool IncludeHidden, FollowSymlinks, NoSkip;
    public bool Quiet, Stats;
    public int MaxDepth = int.MaxValue;
    public int Threads = Environment.ProcessorCount;
    public int MaxResults;
    public HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase);
    public long MinSize;
    public long MaxSize = long.MaxValue;
    public DateTime? After, Before;

    public bool IncludeFiles => !FoldersOnly;
    public bool IncludeDirectories => Folders || FoldersOnly;

    public const string Usage = """
        usage: fq [pattern] [path] [options]

          --glob / --regex      interpret pattern as glob / regex (default: substring)
          --case                case-sensitive match
          --folders             also match folders      --folders-only / --files-only
          --max-depth <n>       0 = top directory only
          --ext <a,b>           extension filter
          --size <+N|-N|N>      at least / at most / exactly (suffix K, M, G)   --min / --max <N>
          --after / --before <YYYY-MM-DD>
          --include-hidden      include dot-names and hidden/system files
          --follow-symlinks     descend into reparse points
          --no-skip             don't skip node_modules, .git, Windows, ...
          --threads <n>   --max-results <n>   --quiet   --stats
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value");

            switch (arg)
            {
                case "--glob": o.Glob = true; break;
                case "--regex": o.Regex = true; break;
                case "--case": o.CaseSensitive = true; break;
                case "--folders": o.Folders = true; break;
                case "--folders-only": o.FoldersOnly = true; break;
                case "--files-only": o.FilesOnly = true; break;
                case "--include-hidden": o.IncludeHidden = true; break;
                case "--follow-symlinks": o.FollowSymlinks = true; break;
                case "--no-skip": o.NoSkip = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--stats": o.Stats = true; break;
                case "--max-depth": o.MaxDepth = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--threads": o.Threads = Math.Max(1, int.Parse(Value(), CultureInfo.InvariantCulture)); break;
                case "--max-results": o.MaxResults = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--min": o.MinSize = ParseSize(Value()); break;
                case "--max": o.MaxSize = ParseSize(Value()); break;
                case "--after": o.After = ParseDate(Value()); break;
                case "--before": o.Before = ParseDate(Value()); break;
                case "--ext":
                    foreach (string e in Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        o.Extensions.Add(e.TrimStart('.'));
                    break;
                case "--size":
                    string s = Value();
                    long size = ParseSize(s.TrimStart('+', '-'));
                    if (s[0] == '+') o.MinSize = size;
                    else if (s[0] == '-') o.MaxSize = size;
                    else o.MinSize = o.MaxSize = size;
                    break;
                default: throw new ArgumentException($"unknown option {arg}");
            }
        }

        if (positional.Count > 2) throw new ArgumentException("too many positional arguments");
        if (positional.Count > 0) o.Pattern = positional[0];
        if (positional.Count > 1) o.Root = positional[1];
        if (o.FilesOnly && o.FoldersOnly) throw new ArgumentException("--files-only conflicts with --folders-only");
        return o;
    }

    static long ParseSize(string text)
    {
        long multiplier = char.ToUpperInvariant(text[^1]) switch
        {
            'K' => 1L << 10,
            'M' => 1L << 20,
            'G' => 1L << 30,
            'T' => 1L << 40,
            _ => 1,
        };
        string digits = multiplier == 1 ? text : text[..^1];
        return (long)(double.Parse(digits, CultureInfo.InvariantCulture) * multiplier);
    }

    static DateTime ParseDate(string text) =>
        DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)
            .ToUniversalTime();
}
