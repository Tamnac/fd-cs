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
            stdout.Write(bytes, 0, count);
        }
    }
}

/// <summary>Per-worker line buffer in front of <see cref="OutputSink"/>.</summary>
sealed class LineBuffer(OutputSink sink)
{
    readonly char[] buffer = new char[1 << 14];
    int length;

    /// <summary>Adds the line <c>directory\name</c> without its first <paramref name="skip"/> characters.</summary>
    public void Add(ReadOnlySpan<char> directory, ReadOnlySpan<char> name, int skip)
    {
        bool separator = directory.Length > 0 && directory[^1] != '\\' && skip <= directory.Length;
        directory = directory[Math.Min(skip, directory.Length)..];
        int lineLength = directory.Length + (separator ? 1 : 0) + name.Length + 1;

        if (lineLength > buffer.Length - length) Flush();
        if (lineLength > buffer.Length)
        {
            sink.Write(string.Concat(directory, separator ? "\\" : "", name, "\n"));
            return;
        }

        directory.CopyTo(buffer.AsSpan(length));
        length += directory.Length;
        if (separator) buffer[length++] = '\\';
        name.CopyTo(buffer.AsSpan(length));
        length += name.Length;
        buffer[length++] = '\n';
    }

    public void Flush()
    {
        if (length == 0) return;
        sink.Write(buffer.AsSpan(0, length));
        length = 0;
    }
}
