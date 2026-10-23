using System.Text;
using Fd;

try
{
    Options options = CommandLine.Parse(args);
    EnterBaseDirectory(options);
    EnsurePatternsAreNotPaths(options);

    List<RootInfo> roots = RootInfo.Resolve(options);
    char separator = ResolveSeparator(options);
    bool colored = UseColor(options);
    var sink = new OutputSink();
    if (options.Quiet) options.MaxResults = 1;
    if (options.ListDetails)
    {
        CommandRunner.RequireLs();
        options.Commands.Add(["ls", "-l", "-h", "-d", colored ? "--color=always" : "--color=never"]);
        options.ExecBatch = true;
    }

    CommandRunner? runner = options.Commands.Count > 0 ? new CommandRunner(options, sink, separator) : null;
    var style = new OutputStyle(
        options.Format is { } format ? FormatTemplate.Parse(format) : null,
        colored ? LsColors.FromEnvironment() : null,
        options.Hyperlink == When.Always || (options.Hyperlink == When.Auto && colored),
        Sanitize: !Console.IsOutputRedirected,
        separator,
        options.Print0 ? '\0' : '\n');

    Func<ResultWriter> createWriter =
        options.Quiet ? () => new NullWriter()
        : runner is not null ? runner.CreateWriter
        : style is { Template: null, Colors: null, Hyperlink: false, Sanitize: false } ? () => new PlainWriter(sink, separator, style.Terminator)
        : () => new FormattedWriter(sink, style);

    if (!Console.IsOutputRedirected) Console.OutputEncoding = Encoding.UTF8;
    runner?.Start();
    long results = new Walker(options, roots, createWriter).Run();
    runner?.Finish();
    return runner is not null ? runner.ExitCode : options.Quiet && results == 0 ? 1 : 0;
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
catch (FdException e)
{
    Console.Error.WriteLine($"[fd error]: {e.Message}");
    return 1;
}

static void EnterBaseDirectory(Options options)
{
    if (options.BaseDirectory is not { } directory) return;
    if (!Directory.Exists(directory)) throw new FdException($"The '--base-directory' path '{directory}' is not a directory.");
    Directory.SetCurrentDirectory(directory);
}

/// <summary>A path-like pattern would silently find nothing, because patterns are matched against names only.</summary>
static void EnsurePatternsAreNotPaths(Options options)
{
    if (options.FullPath) return;
    foreach (string pattern in options.AndPatterns.Append(options.Pattern))
    {
        if (!pattern.Contains('/') && !(pattern.Contains('\\') && Directory.Exists(pattern))) continue;
        throw new FdException($"The search pattern '{pattern}' contains a path-separation character ('{(pattern.Contains('/') ? '/' : '\\')}') and will not lead to any search results.\n\n"
            + $"If you want to search for all files inside the '{pattern}' directory, use a match-all pattern:\n\n  fd . '{pattern}'\n\n"
            + $"Instead, if you want your pattern to match the full file path, use:\n\n  fd --full-path '{pattern}'");
    }
}

/// <summary>--path-separator, or "/" under MSYS and Git Bash, which set MSYSTEM.</summary>
static char ResolveSeparator(Options options)
{
    string? separator = options.PathSeparator ?? (Environment.GetEnvironmentVariable("MSYSTEM") is { Length: > 0 } ? "/" : null);
    if (separator is null) return '\\';
    if (separator.Length != 1) throw new FdException($"A path separator must be exactly one byte, but the given separator is {Encoding.UTF8.GetByteCount(separator)} bytes: '{separator}'.\nIn some shells on Windows, '/' is automatically expanded. Try to use '//' instead.");
    return separator[0];
}

static bool UseColor(Options options) => options.Color switch
{
    When.Always => true,
    When.Never => false,
    _ => Environment.GetEnvironmentVariable("NO_COLOR") is not { Length: > 0 } && !Console.IsOutputRedirected && Terminal.TryEnableAnsi(),
};
