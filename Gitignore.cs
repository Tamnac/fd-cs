using System.Buffers;
using System.Text.RegularExpressions;

namespace Fq;

enum IgnoreMatch { None, Ignore, Whitelist }

/// <summary>
/// One ignore file in gitignore syntax. Of all rules that match an entry, the last one wins.
/// Rules that only look at the entry's name (<c>*.log</c>, <c>node_modules</c>, <c>tmp*</c>) are indexed without the regex engine.
/// </summary>
sealed class Gitignore
{
    static readonly SearchValues<char> GlobCharacters = SearchValues.Create(@"*?[]{}\");

    [ThreadStatic] static char[]? pathBuffer;

    readonly record struct Rule(bool Whitelist, bool DirectoryOnly, Regex? Regex);

    readonly Rule[] rules;
    readonly Dictionary<string, int[]>.AlternateLookup<ReadOnlySpan<char>> byName;
    readonly (string Suffix, int Rule)[] bySuffix;
    readonly (string Prefix, int Rule)[] byPrefix;
    readonly int[] nameRegexes, pathRegexes;

    public bool HasWhitelist { get; }

    Gitignore(Rule[] rules, Dictionary<string, List<int>> names, List<(string, int)> suffixes, List<(string, int)> prefixes,
        List<int> nameRegexes, List<int> pathRegexes)
    {
        this.rules = rules;
        byName = names.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();
        bySuffix = [.. suffixes];
        byPrefix = [.. prefixes];
        this.nameRegexes = [.. nameRegexes];
        this.pathRegexes = [.. pathRegexes];
        HasWhitelist = rules.Any(r => r.Whitelist);
    }

    /// <summary>Returns null if the file is missing, unreadable, or has no rules.</summary>
    public static Gitignore? Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path).Split('\n'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Gitignore? Parse(IEnumerable<string> lines)
    {
        var rules = new List<Rule>();
        var names = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var suffixes = new List<(string, int)>();
        var prefixes = new List<(string, int)>();
        var nameRegexes = new List<int>();
        var pathRegexes = new List<int>();
        bool first = true;

        foreach (string raw in lines)
        {
            string line = first ? raw.TrimStart('\uFEFF') : raw;
            first = false;
            if (line.StartsWith('#')) continue;
            if (!line.EndsWith("\\ ")) line = line.TrimEnd();
            if (line.Length == 0) continue;

            bool whitelist = false, directoryOnly = false, anchored = false;
            if (line.StartsWith("\\!") || line.StartsWith("\\#"))
            {
                line = line[1..];
            }
            else
            {
                if (line.StartsWith('!')) { whitelist = true; line = line[1..]; }
                if (line.StartsWith('/')) { anchored = true; line = line[1..]; }
            }
            if (line.EndsWith('/'))
            {
                directoryOnly = true;
                line = line[..^1];
                if (line.EndsWith('\\')) line = line[..^1];
            }
            if (line.Length == 0) continue;

            string glob = line;
            if (!anchored && !glob.Contains('/') && !glob.StartsWith("**/") && glob != "**") glob = "**/" + glob;
            if (glob.EndsWith("/**")) glob += "/*";

            int index = rules.Count;
            string? nameGlob = glob.StartsWith("**/") && !glob.AsSpan(3).Contains('/') ? glob[3..] : null;
            try
            {
                if (nameGlob is not null && !HasGlobCharacters(nameGlob))
                {
                    rules.Add(new Rule(whitelist, directoryOnly, null));
                    if (!names.TryGetValue(nameGlob, out var list)) names[nameGlob] = list = [];
                    list.Add(index);
                }
                else if (nameGlob is { Length: > 0 } && nameGlob[0] == '*' && !HasGlobCharacters(nameGlob[1..]))
                {
                    rules.Add(new Rule(whitelist, directoryOnly, null));
                    suffixes.Add((nameGlob[1..], index));
                }
                else if (nameGlob is { Length: > 0 } && nameGlob[^1] == '*' && !HasGlobCharacters(nameGlob[..^1]))
                {
                    rules.Add(new Rule(whitelist, directoryOnly, null));
                    prefixes.Add((nameGlob[..^1], index));
                }
                else
                {
                    var regex = Compile(nameGlob ?? glob);
                    rules.Add(new Rule(whitelist, directoryOnly, regex));
                    (nameGlob is null ? pathRegexes : nameRegexes).Add(index);
                }
            }
            catch (Exception e) when (e is FqException or ArgumentException)
            {
                // A malformed pattern is dropped, like in git.
            }
        }

        return rules.Count == 0 ? null : new Gitignore([.. rules], names, suffixes, prefixes, nameRegexes, pathRegexes);
    }

    static bool HasGlobCharacters(string text) => text.AsSpan().ContainsAny(GlobCharacters);

    static Regex Compile(string glob) =>
        new(Glob.ToRegex(glob, literalSeparator: true, backslashEscape: true), RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <param name="directory">The entry's directory relative to the directory of this ignore file, with backslashes.</param>
    public IgnoreMatch Match(ReadOnlySpan<char> directory, ReadOnlySpan<char> name, bool isDirectory)
    {
        int best = -1;

        if (byName.TryGetValue(name, out int[]? named)) best = Best(best, named, isDirectory);
        foreach (var (suffix, rule) in bySuffix)
        {
            if (rule > best && Applies(rule, isDirectory) && name.EndsWith(suffix, StringComparison.Ordinal)) best = rule;
        }
        foreach (var (prefix, rule) in byPrefix)
        {
            if (rule > best && Applies(rule, isDirectory) && name.StartsWith(prefix, StringComparison.Ordinal)) best = rule;
        }
        foreach (int rule in nameRegexes)
        {
            if (rule > best && Applies(rule, isDirectory) && rules[rule].Regex!.IsMatch(name)) best = rule;
        }
        if (pathRegexes.Length > 0)
        {
            ReadOnlySpan<char> path = JoinWithSlashes(directory, name);
            foreach (int rule in pathRegexes)
            {
                if (rule > best && Applies(rule, isDirectory) && rules[rule].Regex!.IsMatch(path)) best = rule;
            }
        }

        return best < 0 ? IgnoreMatch.None : rules[best].Whitelist ? IgnoreMatch.Whitelist : IgnoreMatch.Ignore;
    }

    bool Applies(int rule, bool isDirectory) => isDirectory || !rules[rule].DirectoryOnly;

    int Best(int best, int[] candidates, bool isDirectory)
    {
        foreach (int rule in candidates)
        {
            if (rule > best && Applies(rule, isDirectory)) best = rule;
        }
        return best;
    }

    static ReadOnlySpan<char> JoinWithSlashes(ReadOnlySpan<char> directory, ReadOnlySpan<char> name)
    {
        int length = directory.Length + (directory.Length > 0 ? 1 : 0) + name.Length;
        char[] buffer = pathBuffer is { } existing && existing.Length >= length ? existing : pathBuffer = new char[Math.Max(length, 512)];

        int n = 0;
        foreach (char c in directory) buffer[n++] = c == '\\' ? '/' : c;
        if (directory.Length > 0) buffer[n++] = '/';
        name.CopyTo(buffer.AsSpan(n));
        return buffer.AsSpan(0, length);
    }
}
