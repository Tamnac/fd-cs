using System.Buffers;
using System.Text.RegularExpressions;

namespace Fq;

/// <summary>A compiled search pattern. Plain literals, anchored literals and simple globs avoid the regex engine.</summary>
abstract class NameMatcher
{
    static readonly SearchValues<char> RegexMetacharacters = SearchValues.Create(@"\.^$|?*+()[]{}");
    static readonly SearchValues<char> GlobMetacharacters = SearchValues.Create(@"*?[]{}\");

    public abstract bool IsMatch(ReadOnlySpan<char> text);

    /// <summary>The primary pattern followed by the --and patterns; empty patterns match everything and are left out.</summary>
    public static List<NameMatcher> CreateAll(Options o)
    {
        string[] patterns = [.. o.AndPatterns, o.Pattern];
        string[] regexForms = [.. patterns.Select(p => RegexForm(o, p))];
        bool caseSensitive = o.CaseSensitive ?? regexForms.Any(RustRegex.HasUppercase);

        var matchers = new List<NameMatcher>();
        for (int i = 0; i < patterns.Length; i++)
        {
            if (patterns[i].Length > 0) matchers.Add(Create(o, patterns[i], regexForms[i], caseSensitive));
        }
        return matchers;
    }

    static string RegexForm(Options o, string pattern) =>
        o.Glob && pattern.Length > 0 ? Glob.ToRegex(pattern, literalSeparator: true, backslashEscape: false)
        : o.Exact ? "^" + Regex.Escape(pattern) + "$"
        : o.FixedStrings ? Regex.Escape(pattern)
        : pattern;

    static NameMatcher Create(Options o, string pattern, string regexForm, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (o.Exact) return new TextMatcher(TextMatcher.Mode.Equals, pattern, comparison);
        if (o.FixedStrings) return new TextMatcher(TextMatcher.Mode.Contains, pattern, comparison);
        if (o.Glob) return FromGlob(pattern, regexForm, caseSensitive, comparison);

        return TryParseLiteral(pattern, out var mode, out string? text)
            ? new TextMatcher(mode, text!, comparison)
            : new RegexMatcher(RustRegex.ToDotNet(pattern), caseSensitive);
    }

    // "*.cs", "foo*", "*foo*" and plain names avoid the regex engine.
    static NameMatcher FromGlob(string glob, string regexForm, bool caseSensitive, StringComparison comparison)
    {
        bool starts = glob.StartsWith('*'), ends = glob.EndsWith('*') && glob.Length > 1;
        string core = glob[(starts ? 1 : 0)..(ends ? ^1 : glob.Length)];

        if (!core.AsSpan().ContainsAny(GlobMetacharacters) && !core.Contains('/'))
        {
            var mode = (starts, ends) switch
            {
                (true, true) => TextMatcher.Mode.Contains,
                (true, false) => TextMatcher.Mode.EndsWith,
                (false, true) => TextMatcher.Mode.StartsWith,
                _ => TextMatcher.Mode.Equals,
            };
            return new TextMatcher(mode, core, comparison);
        }

        return new RegexMatcher(regexForm, caseSensitive);
    }

    /// <summary>Recognizes <c>^literal$</c>, <c>^literal</c>, <c>literal$</c> and <c>literal</c>, with escaped punctuation.</summary>
    static bool TryParseLiteral(string pattern, out TextMatcher.Mode mode, out string? text)
    {
        mode = default;
        text = null;
        var literal = new System.Text.StringBuilder();
        int i = 0;
        bool start = pattern.StartsWith('^'), end = false;
        if (start) i++;

        for (; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                if (i + 1 == pattern.Length || char.IsAsciiLetterOrDigit(pattern[i + 1])) return false;
                literal.Append(pattern[++i]);
            }
            else if (c == '$' && i == pattern.Length - 1)
            {
                end = true;
            }
            else if (RegexMetacharacters.Contains(c))
            {
                return false;
            }
            else
            {
                literal.Append(c);
            }
        }

        if (literal.Length == 0) return false;
        mode = (start, end) switch
        {
            (true, true) => TextMatcher.Mode.Equals,
            (true, false) => TextMatcher.Mode.StartsWith,
            (false, true) => TextMatcher.Mode.EndsWith,
            _ => TextMatcher.Mode.Contains,
        };
        text = literal.ToString();
        return true;
    }
}

sealed class TextMatcher(TextMatcher.Mode mode, string text, StringComparison comparison) : NameMatcher
{
    public enum Mode { Equals, StartsWith, EndsWith, Contains }

    public override bool IsMatch(ReadOnlySpan<char> name) => mode switch
    {
        Mode.Equals => name.Equals(text, comparison),
        Mode.StartsWith => name.StartsWith(text, comparison),
        Mode.EndsWith => name.EndsWith(text, comparison),
        _ => name.Contains(text, comparison),
    };
}

sealed class RegexMatcher : NameMatcher
{
    readonly Regex regex;

    public RegexMatcher(string pattern, bool caseSensitive)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        try
        {
            regex = new Regex(pattern, options);
        }
        catch (ArgumentException)
        {
            throw new FqException($"regex parse error: invalid regular expression: {pattern}\n\nNote: You can use the '--fixed-strings' option to search for a literal string instead of a regular expression. Alternatively, you can also use the '--glob' option to match on a glob pattern.");
        }
    }

    public override bool IsMatch(ReadOnlySpan<char> name) => regex.IsMatch(name);
}
