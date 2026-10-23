using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fd;

/// <summary>Bridges Rust regex syntax (what fd users write) to .NET regexes, and decides fd's "smart case".</summary>
static class RustRegex
{
    static readonly Dictionary<string, string> PosixClasses = new()
    {
        ["alpha"] = "a-zA-Z", ["digit"] = "0-9", ["alnum"] = "a-zA-Z0-9", ["upper"] = "A-Z", ["lower"] = "a-z",
        ["space"] = @"\t\n\v\f\r ", ["blank"] = @" \t", ["punct"] = @"!-/:-@\[-`{-~", ["xdigit"] = "0-9A-Fa-f", ["word"] = @"\w",
        ["cntrl"] = @"\x00-\x1F\x7F", ["graph"] = "!-~", ["print"] = " -~", ["ascii"] = @"\x00-\x7F",
    };

    /// <summary>The syntax differences that matter in practice: <c>(?P&lt;n&gt;)</c>, <c>\x{1F600}</c>, <c>\pL</c>, <c>[[:alpha:]]</c>.</summary>
    public static string ToDotNet(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 8);
        bool inClass = false;

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                char e = pattern[i + 1];
                if (e == 'x' && i + 2 < pattern.Length && pattern[i + 2] == '{' && TryReadHexBraces(pattern, i + 2, out int codePoint, out int close))
                {
                    AppendCodePoint(sb, codePoint);
                    i = close;
                }
                else if (e is 'p' or 'P' && i + 2 < pattern.Length && pattern[i + 2] != '{')
                {
                    sb.Append('\\').Append(e).Append('{').Append(pattern[i + 2]).Append('}');
                    i += 2;
                }
                else
                {
                    sb.Append(c).Append(e);
                    i++;
                }
            }
            else if (!inClass && c == '(' && pattern.AsSpan(i).StartsWith("(?P<"))
            {
                sb.Append("(?<");
                i += 3;
            }
            else if (inClass && c == '[' && i + 1 < pattern.Length && pattern[i + 1] == ':' && TryReadPosixClass(pattern, i, out string? expansion, out int end))
            {
                sb.Append(expansion);
                i = end;
            }
            else
            {
                if (c == '[' && !inClass)
                {
                    inClass = true;
                    sb.Append('[');
                    if (i + 1 < pattern.Length && pattern[i + 1] == '^') sb.Append(pattern[++i]);
                    if (i + 1 < pattern.Length && pattern[i + 1] == ']') { sb.Append(@"\]"); i++; }
                    continue;
                }
                if (c == ']') inClass = false;
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    static bool TryReadHexBraces(string p, int open, out int codePoint, out int close)
    {
        close = p.IndexOf('}', open);
        codePoint = 0;
        return close > open + 1 && int.TryParse(p.AsSpan(open + 1, close - open - 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out codePoint)
            && codePoint <= 0x10FFFF;
    }

    static bool TryReadPosixClass(string p, int start, out string? expansion, out int end)
    {
        end = p.IndexOf(":]", start + 2, StringComparison.Ordinal);
        expansion = null;
        return end > 0 && PosixClasses.TryGetValue(p.Substring(start + 2, end - start - 2), out expansion) && (end += 1) > 0;
    }

    static void AppendCodePoint(StringBuilder sb, int codePoint)
    {
        foreach (char unit in char.ConvertFromUtf32(codePoint)) sb.Append(@"\u").Append(((int)unit).ToString("X4", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// fd searches case-sensitively when the pattern contains an uppercase literal or a class with an uppercase range end
    /// (<c>[A-Z]</c>, <c>\w</c>, <c>\p{Lu}</c>). This follows the same rule on the pattern text.
    /// </summary>
    public static bool HasUppercase(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                i++;
                if (EscapeHasUppercase(pattern, ref i)) return true;
            }
            else if (c == '(' && i + 1 < pattern.Length && pattern[i + 1] == '?')
            {
                i += 2;
                if (i < pattern.Length && pattern[i] == 'P') i++;
                if (i < pattern.Length && pattern[i] == '<')
                {
                    i = pattern.IndexOf('>', i);
                    if (i < 0) return false;
                }
                else
                {
                    while (i < pattern.Length && (char.IsAsciiLetter(pattern[i]) || pattern[i] == '-')) i++;
                    i--;
                }
            }
            else if (c == '[')
            {
                if (ClassHasUppercase(pattern, ref i)) return true;
            }
            else if (Rune.TryGetRuneAt(pattern, i, out Rune rune))
            {
                if (Rune.IsUpper(rune)) return true;
                i += rune.Utf16SequenceLength - 1;
            }
        }
        return false;
    }

    static bool ClassHasUppercase(string p, ref int i)
    {
        int j = i + 1;
        if (j < p.Length && p[j] == '^') j++;
        for (bool first = true; j < p.Length; first = false, j++)
        {
            char c = p[j];
            if (c == ']' && !first) break;
            if (c == '[' && j + 1 < p.Length && p[j + 1] == ':')
            {
                int end = p.IndexOf(":]", j + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    if (p.AsSpan(j + 2, end - j - 2) is "alpha" or "upper" or "alnum" or "word" or "xdigit") return true;
                    j = end + 1;
                    continue;
                }
            }
            if (c == '\\' && j + 1 < p.Length)
            {
                j++;
                if (EscapeHasUppercase(p, ref j)) return true;
            }
            else if (char.IsUpper(c))
            {
                return true;
            }
        }
        i = j;
        return false;
    }

    /// <summary>The escape letter is at <paramref name="k"/>. Advances k to the last character of the escape.</summary>
    static bool EscapeHasUppercase(string p, ref int k)
    {
        switch (p[k])
        {
            case 'w' or 'W':
                return true;
            case 'p' or 'P':
                string name;
                if (k + 1 < p.Length && p[k + 1] == '{')
                {
                    int end = p.IndexOf('}', k);
                    if (end < 0) return false;
                    name = p.Substring(k + 2, end - k - 2);
                    k = end;
                }
                else
                {
                    if (k + 1 >= p.Length) return false;
                    name = p[++k].ToString();
                }
                return CategoryHasUppercase(name.TrimStart('^'));
            case 'x' when k + 1 < p.Length && p[k + 1] == '{':
                if (!TryReadHexBraces(p, k + 1, out int codePoint, out int close)) return false;
                k = close;
                return Rune.IsUpper(new Rune(codePoint));
            case 'x' or 'u':
                int digits = p[k] == 'x' ? 2 : 4;
                if (k + digits >= p.Length || !int.TryParse(p.AsSpan(k + 1, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value)) return false;
                k += digits;
                return char.IsUpper((char)value);
            default:
                return false;
        }
    }

    static bool CategoryHasUppercase(string name)
    {
        if (name.Length == 0) return false;
        if (name[0] == 'L') return name is not ("Ll" or "Lowercase_Letter" or "Lowercase");
        if (name.Length <= 2) return false;
        return !(name.StartsWith("Number") || name.StartsWith("Punct") || name.StartsWith("Symbol") || name.StartsWith("Separator")
            || name.StartsWith("Mark") || name.StartsWith("Control") || name.StartsWith("Other") || name.StartsWith("Decimal")
            || name.StartsWith("Digit") || name.StartsWith("Space") || name.StartsWith("White") || name.StartsWith("Lower"));
    }
}

/// <summary>Translates globs to regexes the way the Rust globset crate does: <c>*</c>, <c>?</c>, <c>**</c>, <c>[a-z]</c>, <c>[!a]</c>, <c>{a,b}</c>.</summary>
static class Glob
{
    abstract record Token;
    sealed record Literal(char Value) : Token;
    sealed record Any : Token;
    sealed record ZeroOrMore : Token;
    sealed record RecursivePrefix : Token;
    sealed record RecursiveSuffix : Token;
    sealed record RecursiveZeroOrMore : Token;
    sealed record Class(bool Negated, List<(char Low, char High)> Ranges) : Token;
    sealed record Alternates(List<List<Token>> Branches) : Token;

    /// <summary>
    /// <paramref name="literalSeparator"/>: wildcards do not match <c>/</c>. <paramref name="backslashEscape"/>: a backslash
    /// escapes the next character; otherwise it is a path separator, as on Windows in globset.
    /// Throws <see cref="FdException"/> for malformed globs.
    /// </summary>
    public static string ToRegex(string glob, bool literalSeparator, bool backslashEscape)
    {
        var stack = new List<List<Token>> { new() };
        int p = 0;

        char? Peek() => p < glob.Length ? glob[p] : null;
        char? Bump() => p < glob.Length ? glob[p++] : null;
        void Push(Token token) => stack[^1].Add(token);
        bool IsSeparator(char? c) => c is '/' || (c is '\\' && !backslashEscape);

        while (Bump() is { } c)
        {
            switch (c)
            {
                case '?': Push(new Any()); break;
                case '*': ParseStar(); break;
                case '[': ParseClass(); break;
                case '{':
                    if (stack.Count > 1) throw new FdException($"invalid glob '{glob}': nested alternate groups are not allowed");
                    stack.Add([]);
                    break;
                case '}' when stack.Count > 1:
                    var branches = stack.GetRange(1, stack.Count - 1);
                    stack.RemoveRange(1, stack.Count - 1);
                    Push(new Alternates(branches));
                    break;
                case ',' when stack.Count > 1:
                    stack.Add([]);
                    break;
                case '\\':
                    if (backslashEscape)
                    {
                        if (Bump() is not { } escaped) throw new FdException($"invalid glob '{glob}': dangling '\\'");
                        Push(new Literal(escaped));
                    }
                    else
                    {
                        Push(new Literal('/'));
                    }
                    break;
                default: Push(new Literal(c)); break;
            }
        }
        if (stack.Count != 1) throw new FdException($"invalid glob '{glob}': unclosed alternate group; missing '}}' (maybe escape '{{' with '[{{]'?)");

        if (stack[0] is [RecursivePrefix]) return @"^.*\z";
        var sb = new StringBuilder("^");
        Append(sb, stack[0], literalSeparator);
        return sb.Append(@"\z").ToString();

        void ParseStar()
        {
            char? before = p >= 2 ? glob[p - 2] : null;
            if (Peek() != '*')
            {
                Push(new ZeroOrMore());
                return;
            }
            Bump();
            if (stack[^1].Count == 0)
            {
                if (Peek() is { } next && !IsSeparator(next))
                {
                    Push(new ZeroOrMore());
                    Push(new ZeroOrMore());
                }
                else
                {
                    Push(new RecursivePrefix());
                    if (Peek() is not null) Bump();
                }
                return;
            }

            if (!IsSeparator(before) && (stack.Count <= 1 || before is not (',' or '{')))
            {
                Push(new ZeroOrMore());
                Push(new ZeroOrMore());
                return;
            }

            bool isSuffix;
            if (Peek() is null) isSuffix = true;
            else if (Peek() is ',' or '}' && stack.Count >= 2) isSuffix = true;
            else if (IsSeparator(Peek())) { Bump(); isSuffix = false; }
            else
            {
                Push(new ZeroOrMore());
                Push(new ZeroOrMore());
                return;
            }
            if (IsSeparator(before)) stack[^1].RemoveAt(stack[^1].Count - 1);
            Push(isSuffix ? new RecursiveSuffix() : new RecursiveZeroOrMore());
        }

        void ParseClass()
        {
            var ranges = new List<(char Low, char High)>();
            bool negated = Peek() is '!' or '^';
            if (negated) Bump();
            bool first = true, inRange = false;
            while (true)
            {
                if (Bump() is not { } c) throw new FdException($"invalid glob '{glob}': unclosed character class; missing ']'");
                if (c == ']' && !first) break;
                if (c == ']')
                {
                    ranges.Add((']', ']'));
                }
                else if (c == '-')
                {
                    if (first) ranges.Add(('-', '-'));
                    else if (inRange) { SetRangeEnd('-'); inRange = false; }
                    else inRange = true;
                }
                else
                {
                    if (inRange) SetRangeEnd(c); else ranges.Add((c, c));
                    inRange = false;
                }
                first = false;
            }
            if (inRange) ranges.Add(('-', '-'));
            Push(new Class(negated, ranges));

            void SetRangeEnd(char end)
            {
                var (low, _) = ranges[^1];
                if (end < low) throw new FdException($"invalid glob '{glob}': invalid range '{low}-{end}'");
                ranges[^1] = (low, end);
            }
        }
    }

    static void Append(StringBuilder sb, List<Token> tokens, bool literalSeparator)
    {
        foreach (Token token in tokens)
        {
            switch (token)
            {
                case Literal literal: sb.Append(Regex.Escape(literal.Value.ToString())); break;
                case Any: sb.Append(literalSeparator ? "[^/]" : "."); break;
                case ZeroOrMore: sb.Append(literalSeparator ? "[^/]*" : ".*"); break;
                case RecursivePrefix: sb.Append("(?:/?|.*/)"); break;
                case RecursiveSuffix: sb.Append("/.*"); break;
                case RecursiveZeroOrMore: sb.Append("(?:/|/.*/)"); break;
                case Class cls:
                    sb.Append('[');
                    if (cls.Negated) sb.Append('^');
                    foreach (var (low, high) in cls.Ranges)
                    {
                        AppendClassChar(sb, low);
                        if (low != high) AppendClassChar(sb.Append('-'), high);
                    }
                    sb.Append(']');
                    break;
                case Alternates alternates:
                    sb.Append("(?:");
                    for (int i = 0; i < alternates.Branches.Count; i++)
                    {
                        if (i > 0) sb.Append('|');
                        Append(sb, alternates.Branches[i], literalSeparator);
                    }
                    sb.Append(')');
                    break;
            }
        }
    }

    static void AppendClassChar(StringBuilder sb, char c)
    {
        if (c is '\\' or ']' or '[' or '^' or '-') sb.Append('\\');
        sb.Append(c);
    }
}
