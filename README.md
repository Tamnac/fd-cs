# fd-cs

A fast file finder for Windows that follows the command line and behavior of [fd](https://github.com/sharkdp/fd) 10.5, written in C#.

## Usage:

```
fd [OPTIONS] [pattern] [path]...
```

The pattern is a regular expression matched against file names. Without a pattern but with other options, everything is listed; without any arguments, the help is shown. Without a path, the current directory is searched.

```
fd .                        # everything below the current directory
fd '\.cs$' src              # names ending in .cs under src
fd -g '*.{cs,md}'           # glob instead of regex
fd -e rs -t f -S +10k       # files with extension rs, larger than 10 kB
fd --changed-within 2d      # modified in the last two days
fd -e jpg -x magick '{}' '{.}.png'   # run a command for every result
fd -e cs -X code            # run one command with all results
fd --format '{/.}: {//}'    # custom output
```

## Performance

About **1.4 to 2.1 times faster than fd 10.5.0** when searching a whole drive, and about **20-30x faster than GNU `find`** on a warm file cache and about **55x** on a cold one. This is a relevant comparison when a shell on Windows (Git Bash, as AI coding agents commonly use) has only `find` available.

```
Full crawl, no ignore files             fd -H -I e         424k results
  fd-cs  ████████████████████                              500 ms
  fd     ███████████████████████████████████████████      1064 ms

Name glob, no ignore files              fd -H -I -g *.json  22k results
  fd-cs  █████████████████████                             525 ms
  fd     ███████████████████████████████████████           973 ms

Hidden files, ignore files honored      fd -H e             88k results
  fd-cs  ██████████                                        243 ms
  fd     ███████████████                                   368 ms

Defaults (see note)                     fd e                49k / 60k results
  fd-cs  ████████                                          197 ms
  fd     ███████████                                       281 ms
```

| Search                               | fd-cs  | fd      | Speedup |
| ------------------------------------ | ------ | ------- | ------- |
| `-H -I e` (every file and directory) | 500 ms | 1064 ms | 2.1x    |
| `-H -I -g *.json`                    | 525 ms | 973 ms  | 1.9x    |
| `-H e` (reads `.gitignore` etc.)     | 243 ms | 368 ms  | 1.5x    |
| `e` (defaults)                       | 197 ms | 281 ms  | 1.4x    |

Measurements were on Windows 11 using a ReFS Dev Drive with about 615k entries, on an i7-1165G7 with 8 logical cores.  Compared to fd 10.5.0 from scoop. Mean of 25 runs after 3 warm-up runs, measured with `hyperfine -N`, output discarded. The first three rows return identical results in both tools (`fd-cs` runs with `--no-skip`). 

Note the he last row is not perfectly comparible. `fd-cs` skips a built-in list of directories by default (see below), so it returns fewer results, though the number of files scanned and thus overall it should be similar.

The `find` numbers are for the Git for Windows build of GNU find 4.11 (`find D:/ -name '*e*'`, which runs through the MSYS layer): 12-14 s warm, against about 0.5-0.6 s for `fd -H -I e D:/` with fd-cs. Cold (different drive, empty file cache), `find` took 1 min 33 s and fd-cs 1.7 s. These are single wall-clock runs.

Both tools are very fast, so for ordinary folders, startup cost can take a good fraction of the time and both will feel instant anyway. But hey, speed doesn’t hurt :)

Most of the difference comes from reading directories in large batches straight through the NT API, matching names on the raw directory records without allocating, and sharing work between threads.

## Compatibility with fd

Argument parsing, smart case, regex and glob translation, `.gitignore`/`.ignore`/`.fdignore` handling, size and time filters, `--format`, colors from `LS_COLORS`, hyperlinks, `--exec`, `--exec-batch` and `--list-details` follow fd 10.5. [check.ps1](check.ps1) runs about 190 comparisons against a real `fd` on a generated tree (and optionally on a whole drive) and checks that sorted output and exit status are identical.

Differences:

-  `node_modules`, `.git`, `Windows`, `Program Files`, `bin`, `obj`, `dist`, `build` and 12 other directory names are not searched by default. Use `--no-skip` or `-u` if you don’t want this.
- **No sorting.** Results are printed as they are found, so `--max-results` and `-1` return an arbitrary set of matches.
- **No arguments** shows the help instead of listing everything. Use `fd .` to list everything.
- **Not implemented:** `--gen-completions`, Ctrl-C handling. `--max-buffer-time` is accepted and ignored.
- **`--list-details`** needs GNU `ls` in `PATH`.
- **Help text and errors** are fd-cs's own (`[fd error]: ...`).
- Global ignore files (`core.excludesFile`, `%APPDATA%\fd\ignore`) are approximated.
- Regexes run on the .NET backtracking engine, so a pathological pattern can take very long.

See `fd --help` for all options.

## Build

Needs the .NET 10 SDK and, for NativeAOT, the MSVC build tools.

```
dotnet publish -c Release -r win-x64 -o out-aot
```

The binary is `out-aot/fd.exe`.

## Test

Needs the real `fd` in `PATH` (so do not put `out-aot` in front of it) and PowerShell (Windows PowerShell 5.1 and PowerShell 7 both work):

```
.\check.ps1 .\out-aot\fd.exe
.\check.ps1 .\out-aot\fd.exe -BigRoot D:\    # also compares full-drive walks
```
