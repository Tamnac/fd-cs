using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Fd;

/// <summary>
/// --exec runs the command templates for every result on a pool of threads, buffering the output of each run so that it is never
/// interleaved; --exec-batch collects all results and runs one command per batch of arguments.
/// </summary>
sealed class CommandRunner
{
    const int CommandLineLimit = 30000;

    readonly Options options;
    readonly OutputSink sink;
    readonly char separator;
    readonly List<List<FormatTemplate>> commands = [];
    readonly BlockingCollection<string> queue;
    readonly List<string> collected = [];
    readonly Stream stderr = Console.OpenStandardError();
    Thread[] executors = [];
    int failed;

    public bool Batch => options.ExecBatch;
    public int ExitCode => Volatile.Read(ref failed) != 0 ? 1 : 0;

    public CommandRunner(Options options, OutputSink sink, char separator)
    {
        this.options = options;
        this.sink = sink;
        this.separator = separator;
        queue = new BlockingCollection<string>(Math.Max(2, options.Threads * 2));

        foreach (List<string> words in options.Commands)
        {
            var templates = words.Select(FormatTemplate.Parse).ToList();
            if (Batch && templates[0].HasPlaceholders)
                throw new FdException("First argument of --exec-batch must be a fixed executable, not a placeholder");
            if (!templates.Any(t => t.HasPlaceholders)) templates.Add(FormatTemplate.Parse("{}"));
            if (Batch && templates.Count(t => t.HasPlaceholders) > 1)
                throw new FdException("Only one placeholder allowed for batch commands");
            commands.Add(templates);
        }
    }

    /// <summary>Checks that GNU ls is available, which --list-details runs.</summary>
    public static void RequireLs()
    {
        try
        {
            using var ls = Process.Start(new ProcessStartInfo("ls", "--version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            ls.WaitForExit();
        }
        catch (Win32Exception)
        {
            throw new FdException("'fd --list-details' is not supported on Windows unless GNU 'ls' is installed.");
        }
    }

    public ResultWriter CreateWriter() => new QueueWriter(this);

    public void Start()
    {
        if (Batch) return;
        executors = new Thread[options.Threads];
        for (int i = 0; i < executors.Length; i++)
        {
            executors[i] = new Thread(ExecuteQueued);
            executors[i].Start();
        }
    }

    /// <summary>Call after the walk: waits for the queued commands, or runs the batches.</summary>
    public void Finish()
    {
        queue.CompleteAdding();
        foreach (Thread executor in executors) executor.Join();
        if (Batch) RunBatches();
    }

    void Submit(string path)
    {
        if (Batch)
        {
            lock (collected) collected.Add(path);
        }
        else
        {
            queue.Add(path);
        }
    }

    void ExecuteQueued()
    {
        foreach (string path in queue.GetConsumingEnumerable())
        {
            bool buffer = options.Threads > 1;
            var outputs = new List<(byte[] Out, byte[] Err)>();
            foreach (List<FormatTemplate> command in commands)
            {
                string[] words = [.. command.Select(t => t.Generate(path, separator))];
                if (!Run(words, buffer, outputs)) { Interlocked.Exchange(ref failed, 1); break; }
            }

            lock (sink)
            {
                foreach (var (stdout, stderrBytes) in outputs)
                {
                    sink.WriteBytes(stdout);
                    stderr.Write(stderrBytes);
                }
                if (options.Print0) sink.WriteBytes([0]);
            }
        }
    }

    /// <summary>Runs one command line. Returns false if it could not be started or exited with a non-zero status.</summary>
    bool Run(string[] words, bool buffer, List<(byte[], byte[])> outputs)
    {
        var start = new ProcessStartInfo(words[0]) { UseShellExecute = false, RedirectStandardOutput = buffer, RedirectStandardError = buffer };
        foreach (string word in words.Skip(1)) start.ArgumentList.Add(word);

        try
        {
            using Process process = Process.Start(start)!;
            if (buffer)
            {
                var error = new MemoryStream();
                var output = new MemoryStream();
                Task errorCopy = process.StandardError.BaseStream.CopyToAsync(error);
                process.StandardOutput.BaseStream.CopyTo(output);
                errorCopy.Wait();
                outputs.Add((output.ToArray(), error.ToArray()));
            }
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception e)
        {
            Console.Error.WriteLine(e.NativeErrorCode == 2
                ? $"[fd error]: Command not found: {words[0]}"
                : $"[fd error]: Problem while executing command: {e.Message}");
            return false;
        }
    }

    void RunBatches()
    {
        foreach (List<FormatTemplate> command in commands)
        {
            int pathIndex = command.FindIndex(t => t.HasPlaceholders);
            string[] before = [.. command.Take(pathIndex).Select(t => t.Generate(""))];
            string[] after = [.. command.Skip(pathIndex + 1).Select(t => t.Generate(""))];
            int fixedLength = before.Concat(after).Sum(w => w.Length + 3);

            var batch = new List<string>();
            int length = fixedLength;
            foreach (string path in collected)
            {
                string word = command[pathIndex].Generate(path, separator);
                bool full = (options.BatchSize > 0 && batch.Count >= options.BatchSize) || (batch.Count > 0 && length + word.Length + 3 > CommandLineLimit);
                if (full)
                {
                    RunBatch(before, batch, after);
                    batch.Clear();
                    length = fixedLength;
                }
                batch.Add(word);
                length += word.Length + 3;
            }
            if (batch.Count > 0) RunBatch(before, batch, after);
        }
    }

    void RunBatch(string[] before, List<string> paths, string[] after)
    {
        if (!Run([.. before, .. paths, .. after], buffer: false, outputs: [])) Interlocked.Exchange(ref failed, 1);
    }

    sealed class QueueWriter(CommandRunner runner) : ResultWriter
    {
        public override void Add(RootInfo root, string directory, ReadOnlySpan<char> name, EntryKinds kind) =>
            runner.Submit(DisplayPath(root, directory, name));
    }
}
