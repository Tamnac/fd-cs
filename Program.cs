using System.Diagnostics;
using System.Text;
using Fq;

Options options;
try
{
    if (args.Length == 1 && args[0] is "-h" or "--help") { Console.WriteLine(Options.Usage); return 0; }
    options = Options.Parse(args);
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"fq: {e.Message}\n{Options.Usage}");
    return 2;
}

if (!Directory.Exists(options.Root))
{
    Console.Error.WriteLine($"fq: no such directory: {options.Root}");
    return 2;
}

if (!Console.IsOutputRedirected) Console.OutputEncoding = Encoding.UTF8;

long start = Stopwatch.GetTimestamp();
WalkStats stats = new Walker(options).Run();

if (options.Stats)
{
    Console.Error.WriteLine(
        $"{stats.Results} results, {stats.Directories} directories, {options.Threads} threads, {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms");
}
return stats.Results > 0 ? 0 : 1;
