using System.Text;
using Fq;

try
{
    Options options = CommandLine.Parse(args);
    EnterBaseDirectory(options);
    EnsurePatternsAreNotPaths(options);

    List<RootInfo> roots = RootInfo.Resolve(options);
    char separator = ResolveSeparator(options);
    var sink = new OutputSink();
    if (options.Quiet) options.MaxResults = 1;

    Func<ResultWriter> createWriter = options.Quiet
        ? () => new NullWriter()
        : () => new PlainWriter(sink, separator, options.Print0 ? '\0' : '\n');

    if (!Console.IsOutputRedirected) Console.OutputEncoding = Encoding.UTF8;
    long results = new Walker(options, roots, createWriter).Run();
    return options.Quiet && results == 0 ? 1 : 0;
}
catch (HelpRequested help)
{
    Console.Out.WriteLine(help.Message);
    return 0;
}
catch (UsageException e)
{
    Console.Error.WriteLine($"error: {e.Message}\n\nFor more information, try '--help'.");
    return 2;
}
catch (FqException e)
{
    Console.Error.WriteLine($"[fq error]: {e.Message}");
    return 1;
}

static void EnterBaseDirectory(Options options)
{
    if (options.BaseDirectory is not { } directory) return;
    if (!Directory.Exists(directory)) throw new FqException($"The '--base-directory' path '{directory}' is not a directory.");
    Directory.SetCurrentDirectory(directory);
}

/// <summary>A path-like pattern would silently find nothing, because patterns are matched against names only.</summary>
static void EnsurePatternsAreNotPaths(Options options)
{
    if (options.FullPath) return;
    foreach (string pattern in options.AndPatterns.Append(options.Pattern))
    {
        if (!pattern.Contains('/') && !(pattern.Contains('\\') && Directory.Exists(pattern))) continue;
        throw new FqException($"The search pattern '{pattern}' contains a path-separation character ('{(pattern.Contains('/') ? '/' : '\\')}') and will not lead to any search results.\n\n"
            + $"If you want to search for all files inside the '{pattern}' directory, use a match-all pattern:\n\n  fq . '{pattern}'\n\n"
            + $"Instead, if you want your pattern to match the full file path, use:\n\n  fq --full-path '{pattern}'");
    }
}

/// <summary>--path-separator, or "/" under MSYS and Git Bash, which set MSYSTEM.</summary>
static char ResolveSeparator(Options options)
{
    string? separator = options.PathSeparator ?? (Environment.GetEnvironmentVariable("MSYSTEM") is { Length: > 0 } ? "/" : null);
    if (separator is null) return '\\';
    if (separator.Length != 1) throw new FqException($"A path separator must be exactly one byte, but the given separator is {Encoding.UTF8.GetByteCount(separator)} bytes: '{separator}'.\nIn some shells on Windows, '/' is automatically expanded. Try to use '//' instead.");
    return separator[0];
}
