using System.Globalization;
using System.Text;

namespace Fq;

/// <summary>A terminal style as <c>LS_COLORS</c> describes it, written the way fd's ANSI library does.</summary>
readonly record struct AnsiStyle(string Prefix)
{
    public bool IsPlain => Prefix.Length == 0;

    /// <summary>Parses codes such as <c>1;38;5;203</c>. Returns null if a code is not a number or is unknown.</summary>
    public static AnsiStyle? Parse(string codes)
    {
        bool bold = false, dimmed = false, italic = false, underline = false, blink = false, reverse = false, hidden = false, strike = false;
        string? foreground = null, background = null;
        string[] parts = codes.Split(';');

        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int code)) return null;
            switch (code)
            {
                case 0: bold = dimmed = italic = underline = blink = reverse = hidden = strike = false; foreground = background = null; break;
                case 1: bold = true; break;
                case 2: dimmed = true; break;
                case 3: italic = true; break;
                case 4: underline = true; break;
                case 5 or 6: blink = true; break;
                case 7: reverse = true; break;
                case 8: hidden = true; break;
                case 9: strike = true; break;
                case 39: foreground = null; break;
                case 49: background = null; break;
                case (>= 30 and <= 37) or (>= 90 and <= 97): foreground = parts[i]; break;
                case (>= 40 and <= 47) or (>= 100 and <= 107): background = parts[i]; break;
                case 38 or 48:
                    string? color = ExtendedColor(parts, ref i);
                    if (color is null) return null;
                    if (code == 38) foreground = color; else background = color;
                    break;
                default: return null;
            }
        }

        var attributes = new List<string>();
        if (bold) attributes.Add("1");
        if (dimmed) attributes.Add("2");
        if (italic) attributes.Add("3");
        if (underline) attributes.Add("4");
        if (blink) attributes.Add("5");
        if (reverse) attributes.Add("7");
        if (hidden) attributes.Add("8");
        if (strike) attributes.Add("9");
        if (background is not null) attributes.Add(background);
        if (foreground is not null) attributes.Add(foreground);
        return new AnsiStyle(attributes.Count == 0 ? "" : "\u001b[" + string.Join(';', attributes) + "m");
    }

    /// <summary>Reads <c>5;n</c> or <c>2;r;g;b</c> after a 38 or 48 at <paramref name="i"/> and returns the complete code.</summary>
    static string? ExtendedColor(string[] parts, ref int i)
    {
        if (i + 2 < parts.Length && parts[i + 1] == "5" && byte.TryParse(parts[i + 2], out _))
        {
            string color = $"{parts[i]};5;{parts[i + 2]}";
            i += 2;
            return color;
        }
        if (i + 4 < parts.Length && parts[i + 1] == "2" && byte.TryParse(parts[i + 2], out _) && byte.TryParse(parts[i + 3], out _) && byte.TryParse(parts[i + 4], out _))
        {
            string color = $"{parts[i]};2;{parts[i + 2]};{parts[i + 3]};{parts[i + 4]}";
            i += 4;
            return color;
        }
        return null;
    }

    public void Paint(StringBuilder sb, ReadOnlySpan<char> text)
    {
        if (IsPlain)
        {
            sb.Append(text);
            return;
        }
        sb.Append(Prefix).Append(text).Append("\u001b[0m");
    }
}

/// <summary>Styles for directories, links and files by name, from <c>LS_COLORS</c> or fd's default palette.</summary>
sealed class LsColors
{
    /// <summary>What GNU ls uses for anything LS_COLORS does not define; fd's color library starts from it too.</summary>
    const string GnuDefaults = "rs=0:di=01;34:ln=01;36:mh=00:pi=40;33:so=01;35:do=01;35:bd=40;33;01:cd=40;33;01:or=40;31;01:mi=00:su=37;41:sg=30;43:ca=30;41:tw=30;42:ow=34;42:st=37;44:ex=01;32";

    readonly Dictionary<string, AnsiStyle> indicators = new(StringComparer.Ordinal);
    readonly List<(string Suffix, AnsiStyle Style)> suffixes = [];

    public static LsColors FromEnvironment() => new(Environment.GetEnvironmentVariable("LS_COLORS") ?? DefaultLsColors.Value);

    public LsColors(string definition)
    {
        foreach (string entry in (GnuDefaults + ":" + definition).Split(':'))
        {
            int equals = entry.IndexOf('=');
            if (equals <= 0 || AnsiStyle.Parse(entry[(equals + 1)..]) is not { } style) continue;

            string key = entry[..equals];
            if (key[0] == '*') suffixes.Add((key[1..], style));
            else indicators[key] = style;
        }
    }

    public AnsiStyle? Directory => indicators.TryGetValue("di", out var style) ? style : null;

    /// <summary>Directories and links by their indicator, files by the last matching name pattern (ignoring case), then <c>fi</c>.</summary>
    public AnsiStyle? For(EntryKinds kind, ReadOnlySpan<char> name)
    {
        if (kind == EntryKinds.Directory) return Directory ?? Indicator("no");
        if (kind == EntryKinds.Link) return Indicator("ln") ?? Indicator("no");

        for (int i = suffixes.Count - 1; i >= 0; i--)
        {
            if (name.EndsWith(suffixes[i].Suffix, StringComparison.OrdinalIgnoreCase)) return suffixes[i].Style;
        }
        return Indicator("fi") ?? Indicator("no");
    }

    AnsiStyle? Indicator(string key) => indicators.TryGetValue(key, out var style) ? style : null;
}
