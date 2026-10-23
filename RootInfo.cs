namespace Fq;

/// <summary>A search root: where it is, how its results are prefixed on output, and the ignore files above it.</summary>
sealed class RootInfo
{
    public required string FullPath { get; init; }

    /// <summary>Printed before the path relative to the root, including a trailing separator. Empty when the working directory prefix is stripped.</summary>
    public required string Prefix { get; init; }

    /// <summary>Number of leading characters of a directory below <see cref="FullPath"/> that make up the root.</summary>
    public int Skip => FullPath.Length + (FullPath.EndsWith('\\') ? 0 : 1);

    public IgnoreLevel? ParentIgnore { get; set; }
    public uint VolumeSerial { get; set; }

    /// <summary>
    /// Resolves the roots given on the command line. Paths that are not directories are reported and left out, like fd does;
    /// without paths the current directory is searched.
    /// </summary>
    public static List<RootInfo> Resolve(Options options)
    {
        bool defaultRoot = options.Paths.Count == 0;
        bool stripCwd = defaultRoot && options.StripCwdPrefix switch
        {
            When.Always => true,
            When.Never => false,
            _ => !(options.Print0 || options.Commands.Count > 0 || options.ListDetails),
        };

        var roots = new List<RootInfo>();
        foreach (string given in defaultRoot ? ["."] : options.Paths)
        {
            if (!Directory.Exists(given))
            {
                Console.Error.WriteLine($"[fq error]: Search path '{given}' is not a directory.");
                continue;
            }

            string full = Path.GetFullPath(given);
            if (full.Length > 3 && full.EndsWith('\\')) full = full.TrimEnd('\\');

            string prefix = options.AbsolutePath ? full
                : given == "." ? "."
                : given.Replace('/', '\\').TrimEnd('\\');
            if (stripCwd) prefix = "";
            else if (!prefix.EndsWith('\\')) prefix += '\\';

            roots.Add(new RootInfo { FullPath = full, Prefix = prefix });
        }

        if (roots.Count == 0) throw new FqException("No valid search paths given.");
        return roots;
    }
}
