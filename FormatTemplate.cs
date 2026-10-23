using System.Text;

namespace Fq;

/// <summary>
/// A template with the placeholders <c>{}</c> (path), <c>{/}</c> (file name), <c>{//}</c> (parent directory),
/// <c>{.}</c> (path without extension) and <c>{/.}</c> (file name without extension). <c>{{</c> and <c>}}</c> are literal braces.
/// </summary>
sealed class FormatTemplate
{
    enum Kind { Text, Path, Basename, Parent, NoExtension, BasenameNoExtension }

    static readonly (string Text, Kind Kind)[] Placeholders =
        [("{}", Kind.Path), ("{/}", Kind.Basename), ("{//}", Kind.Parent), ("{.}", Kind.NoExtension), ("{/.}", Kind.BasenameNoExtension)];

    readonly List<(Kind Kind, string Text)> tokens = [];

    /// <summary>False if the template is plain text.</summary>
    public bool HasPlaceholders => tokens.Any(t => t.Kind != Kind.Text);

    public static FormatTemplate Parse(string format)
    {
        var template = new FormatTemplate();
        var text = new StringBuilder();
        int i = 0;

        while (i < format.Length)
        {
            ReadOnlySpan<char> rest = format.AsSpan(i);
            if (rest.StartsWith("{{") || rest.StartsWith("}}"))
            {
                text.Append(format[i]);
                i += 2;
                continue;
            }

            string? placeholder = null;
            Kind kind = Kind.Text;
            foreach (var candidate in Placeholders)
            {
                if (!rest.StartsWith(candidate.Text)) continue;
                (placeholder, kind) = candidate;
                break;
            }

            if (placeholder is null)
            {
                text.Append(format[i++]);
            }
            else if (i + placeholder.Length < format.Length && format[i + placeholder.Length] == '}')
            {
                // The closing brace of the placeholder is followed by an escaping one: the placeholder is literal text.
                text.Append(placeholder);
                i += placeholder.Length + 1;
            }
            else
            {
                if (text.Length > 0) template.tokens.Add((Kind.Text, text.ToString()));
                text.Clear();
                template.tokens.Add((kind, ""));
                i += placeholder.Length;
            }
        }

        if (text.Length > 0) template.tokens.Add((Kind.Text, text.ToString()));
        return template;
    }

    /// <param name="separator">Replaces the backslashes in what the placeholders expand to; literal text is left alone.</param>
    public string Generate(string path, char separator = '\\')
    {
        var sb = new StringBuilder();
        foreach (var (kind, text) in tokens)
        {
            string expansion = kind switch
            {
                Kind.Path => path,
                Kind.Basename => Basename(path),
                Kind.Parent => Parent(path),
                Kind.NoExtension => WithoutExtension(path),
                Kind.BasenameNoExtension => Stem(Basename(path)),
                _ => text,
            };
            sb.Append(kind == Kind.Text ? expansion : expansion.Replace('\\', separator));
        }
        return sb.ToString();
    }


    static bool IsSeparator(char c) => c is '\\' or '/';

    static string Basename(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return path;
        int last = trimmed.LastIndexOfAny(['\\', '/']);
        string name = trimmed[(last + 1)..];
        return name is ".." || (last < 0 && name.EndsWith(':')) ? path : name;
    }

    /// <summary>The parent directory; "." for a bare name; the path itself for a root.</summary>
    static string Parent(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        int last = trimmed.LastIndexOfAny(['\\', '/']);
        if (trimmed.Length == 0 || (last < 0 && trimmed.EndsWith(':'))) return path;
        if (last < 0) return ".";

        string parent = trimmed[..last].TrimEnd('\\', '/');
        if (parent.Length == 0 || parent.EndsWith(':')) return trimmed[..(last + 1)];
        return parent;
    }

    /// <summary>The file name without its extension; a leading dot is part of the name.</summary>
    static string Stem(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || name is ".." ? name : name[..dot];
    }

    static string WithoutExtension(string path)
    {
        string result = Path.Join(Parent(path), Stem(Basename(path)));
        return result.Length > 2 && result[0] == '.' && IsSeparator(result[1]) ? result[2..] : result;
    }
}
