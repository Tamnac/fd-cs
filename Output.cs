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
