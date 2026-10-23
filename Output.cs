using System.Text;

namespace Fq;

/// <summary>Shared stdout. Writes are whole chunks, so lines from different threads never interleave.</summary>
sealed class OutputSink
{
    readonly Stream stdout = Console.OpenStandardOutput();
    readonly Lock gate = new();
    byte[] bytes = new byte[1 << 16];

    public void Write(ReadOnlySpan<char> text)
    {
        lock (gate)
        {
            int max = Encoding.UTF8.GetMaxByteCount(text.Length);
            if (bytes.Length < max) bytes = new byte[max];
            int count = Encoding.UTF8.GetBytes(text, bytes);
            WriteBytes(bytes.AsSpan(0, count));
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            try
            {
                stdout.Write(data);
            }
            catch (IOException)
            {
                Environment.Exit(0);    // the reader closed the pipe, e.g. `| head`
            }
        }
    }
}

/// <summary>Per-worker consumer of results. Created once per walker thread, so implementations need no locking of their own.</summary>
abstract class ResultWriter
{
    public abstract void Add(RootInfo root, string directory, ReadOnlySpan<char> name, EntryKinds kind);
    public virtual void Flush() { }

    /// <summary>The path as printed: the root as given, then the path relative to it. Directories get a trailing separator only in <see cref="PlainWriter"/>.</summary>
    public static string DisplayPath(RootInfo root, string directory, ReadOnlySpan<char> name)
    {
        ReadOnlySpan<char> relative = directory.Length > root.Skip ? directory.AsSpan(root.Skip) : [];
        return string.Concat(root.Prefix, relative, relative.Length > 0 ? "\\" : "", name);
    }
}

/// <summary>Counts results only; used by --quiet.</summary>
sealed class NullWriter : ResultWriter
{
    public override void Add(RootInfo root, string directory, ReadOnlySpan<char> name, EntryKinds kind) { }
}

/// <summary>
/// Formats a result as <c>prefix\relative\name</c> (the directory relative to its root, without the root's first characters),
/// straight from the directory record into a buffer that is written to stdout in chunks.
/// </summary>
sealed class PlainWriter(OutputSink sink, char separator, char terminator) : ResultWriter
{
    readonly char[] buffer = new char[1 << 14];
    int length;

    public override void Add(RootInfo root, string directory, ReadOnlySpan<char> name, EntryKinds kind)
    {
        ReadOnlySpan<char> relative = directory.Length > root.Skip ? directory.AsSpan(root.Skip) : [];
        int maxLength = root.Prefix.Length + relative.Length + name.Length + 3;

        if (maxLength > buffer.Length - length) Flush();
        if (maxLength > buffer.Length)
        {
            char[] large = new char[maxLength];
            sink.Write(large.AsSpan(0, Format(large, root.Prefix, relative, name, kind)));
            return;
        }
        length += Format(buffer.AsSpan(length), root.Prefix, relative, name, kind);
    }

    public override void Flush()
    {
        if (length == 0) return;
        sink.Write(buffer.AsSpan(0, length));
        length = 0;
    }

    int Format(Span<char> destination, string prefix, ReadOnlySpan<char> relative, ReadOnlySpan<char> name, EntryKinds kind)
    {
        int n = 0;
        prefix.CopyTo(destination);
        n += prefix.Length;
        if (relative.Length > 0)
        {
            relative.CopyTo(destination[n..]);
            n += relative.Length;
            destination[n++] = '\\';
        }
        name.CopyTo(destination[n..]);
        n += name.Length;
        if (kind == EntryKinds.Directory) destination[n++] = '\\';
        if (separator != '\\') destination[..n].Replace('\\', separator);
        destination[n++] = terminator;
        return n;
    }
}

/// <summary>What a <see cref="FormattedWriter"/> prints and how.</summary>
sealed record OutputStyle(FormatTemplate? Template, LsColors? Colors, bool Hyperlink, bool Sanitize, char Separator, char Terminator);

/// <summary>The slower writer for --format, colors, hyperlinks and terminal output: builds each result as a string.</summary>
sealed class FormattedWriter(OutputSink sink, OutputStyle style) : ResultWriter
{
    static readonly AnsiStyle Plain = new("");
    readonly StringBuilder text = new();

    public override void Add(RootInfo root, string directory, ReadOnlySpan<char> name, EntryKinds kind)
    {
        ReadOnlySpan<char> relative = directory.Length > root.Skip ? directory.AsSpan(root.Skip) : [];
        if (style.Hyperlink) text.Append("\u001b]8;;").Append(FileUrl(directory, name)).Append("\u001b\\");

        if (style.Template is { } template)
        {
            text.Append(Prepare(template.Generate(DisplayPath(root, directory, name), style.Separator), separate: false));
        }
        else if (style.Colors is { } colors)
        {
            string parent = string.Concat(root.Prefix, relative, relative.Length > 0 ? "\\" : "");
            AnsiStyle directoryStyle = colors.Directory ?? Plain;
            if (parent.Length > 0) directoryStyle.Paint(text, Prepare(parent));
            (colors.For(kind, name) ?? Plain).Paint(text, Prepare(name.ToString()));
            if (kind == EntryKinds.Directory) directoryStyle.Paint(text, style.Separator.ToString());
        }
        else
        {
            text.Append(Prepare(DisplayPath(root, directory, name)));
            if (kind == EntryKinds.Directory) text.Append(style.Separator);
        }

        if (style.Hyperlink) text.Append("\u001b]8;;\u001b\\");
        text.Append(style.Terminator);
        if (text.Length >= 1 << 14) Flush();
    }

    public override void Flush()
    {
        if (text.Length == 0) return;
        sink.Write(text.ToString());
        text.Clear();
    }

    string Prepare(string value, bool separate = true)
    {
        if (separate && style.Separator != '\\') value = value.Replace('\\', style.Separator);
        return style.Sanitize ? Sanitizer.Escape(value) : value;
    }

    /// <summary>file:///C:/dir/name with everything but unreserved characters percent-encoded, as UTF-8.</summary>
    static string FileUrl(string directory, ReadOnlySpan<char> name)
    {
        var url = new StringBuilder("file:///");
        string path = directory.EndsWith('\\') ? directory + name.ToString() : directory + "\\" + name.ToString();
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            if (b is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (byte)'/' or (byte)':' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
                url.Append((char)b);
            else if (b == (byte)'\\')
                url.Append('/');
            else
                url.Append('%').Append(b.ToString("X2"));
        }
        return url.ToString();
    }
}

/// <summary>Makes file names safe to print to a terminal: control and invisible characters are written as escapes.</summary>
static class Sanitizer
{
    public static string Escape(string value)
    {
        if (!value.EnumerateRunes().Any(NeedsEscape)) return value;

        var sb = new StringBuilder(value.Length + 8);
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (!NeedsEscape(rune)) sb.Append(rune.ToString());
            else if (rune.Value <= 0xFF) sb.Append(@"\x").Append(rune.Value.ToString("X2"));
            else sb.Append(@"\u{").Append(rune.Value.ToString("X4")).Append('}');
        }
        return sb.ToString();
    }

    static bool NeedsEscape(Rune rune) =>
        rune.Value != '\t' && (Rune.IsControl(rune) || rune.Value is 0xAD or 0x180E or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E)
            or (>= 0x2060 and <= 0x206F) or 0xFEFF or (>= 0xFFF9 and <= 0xFFFB) or (>= 0xE0000 and <= 0xE007F));
}
