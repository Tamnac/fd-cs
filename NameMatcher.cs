using System.Text;
using System.Text.RegularExpressions;

namespace Fq;

abstract class NameMatcher
{
    public abstract bool IsMatch(ReadOnlySpan<char> name);

    public static NameMatcher? Create(Options o)
    {
        if (o.Pattern.Length == 0) return null;
        var comparison = o.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (o.Regex) return new RegexMatcher(o.Pattern, o.CaseSensitive);
        if (!o.Glob) return new TextMatcher(TextMatcher.Mode.Contains, o.Pattern, comparison);
        return FromGlob(o.Pattern, o.CaseSensitive, comparison);
    }

    // "*.cs", "foo*", "*foo*" and plain names avoid the regex engine.
    static NameMatcher FromGlob(string glob, bool caseSensitive, StringComparison comparison)
    {
        bool starts = glob.StartsWith('*'), ends = glob.EndsWith('*') && glob.Length > 1;
        string core = glob[(starts ? 1 : 0)..(ends ? ^1 : glob.Length)];

        if (core.AsSpan().IndexOfAny("*?[") < 0)
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

        return new RegexMatcher(GlobToRegex(glob), caseSensitive);
    }

    static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        foreach (char c in glob)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                '[' or ']' => c.ToString(),
                _ => Regex.Escape(c.ToString()),
            });
        }
        return sb.Append('$').ToString();
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
        regex = new Regex(pattern, options);
    }

    public override bool IsMatch(ReadOnlySpan<char> name) => regex.IsMatch(name);
}
