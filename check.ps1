<#
.SYNOPSIS
Compares fd-cs with the real fd on a generated fixture tree: same arguments, sorted output, exit codes.

.DESCRIPTION
The real fd must be in PATH (and must not be the executable under test). fd-cs runs with --no-skip, because fd has no skip list.
Works with Windows PowerShell 5.1 and PowerShell 7. Run it with:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\check.ps1 .\out-aot\fd.exe

.PARAMETER Exe
The fd-cs executable to test.

.PARAMETER Fixture
Where the fixture tree is generated. It is deleted and recreated.

.PARAMETER BigRoot
A directory (for example D:\) to also compare full walks of.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Fixture = (Join-Path ([IO.Path]::GetTempPath()) 'fd-fixture'),
    [string]$BigRoot
)

$ErrorActionPreference = 'Stop'

$Exe = (Resolve-Path -LiteralPath $Exe).ProviderPath
$Fixture = [IO.Path]::GetFullPath($Fixture)
$RealFd = Get-Command fd -CommandType Application -ErrorAction SilentlyContinue | Where-Object { $_.Source -ne $Exe } | Select-Object -First 1 -ExpandProperty Source
if (-not $RealFd) { Write-Host 'fd not found in PATH'; exit 2 }

$Utf8 = New-Object Text.UTF8Encoding $false
$cafe = "caf$([char]0xE9)"
$nihon = "$([char]0x65E5)$([char]0x672C)"

# ---- running programs ----

function ConvertTo-Argument([string]$Text) {
    if ($Text.Length -gt 0 -and $Text -notmatch '[\s"]') { return $Text }
    $builder = New-Object Text.StringBuilder '"'
    $backslashes = 0
    foreach ($c in $Text.ToCharArray()) {
        if ($c -eq [char]'\') {
            $backslashes++
        } elseif ($c -eq [char]'"') {
            [void]$builder.Append('\' * ($backslashes * 2 + 1)).Append('"')
            $backslashes = 0
        } else {
            [void]$builder.Append('\' * $backslashes).Append($c)
            $backslashes = 0
        }
    }
    [void]$builder.Append('\' * ($backslashes * 2)).Append('"')
    $builder.ToString()
}

function Invoke-Native([string]$File, [string[]]$Arguments, [string]$Directory, [hashtable]$Environment) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $File
    $info.Arguments = (@($Arguments | ForEach-Object { ConvertTo-Argument $_ })) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $Utf8
    $info.StandardErrorEncoding = $Utf8
    if ($Directory) { $info.WorkingDirectory = $Directory }
    if ($Environment) {
        foreach ($name in $Environment.Keys) { $info.EnvironmentVariables[$name] = $Environment[$name] }
    }

    $process = [Diagnostics.Process]::Start($info)
    $process.StandardInput.Close()
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    [pscustomobject]@{ Out = $output; Err = $errors.Result; Code = $process.ExitCode }
}

# ---- fixture ----

function P([string]$Relative) { Join-Path $Fixture $Relative }
function Make-Directory([string[]]$Names) { foreach ($n in $Names) { [void][IO.Directory]::CreateDirectory((P $n)) } }

function Touch([string[]]$Names) {
    foreach ($n in $Names) {
        $path = P $n
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        if (-not [IO.File]::Exists($path)) { [IO.File]::WriteAllBytes($path, [byte[]]@()) }
    }
}

function Write-Text([string]$Name, [string[]]$Lines) {
    $path = P $Name
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, (($Lines -join "`n") + "`n"), $Utf8)
}

function Remove-Fixture {
    if (-not (Test-Path -LiteralPath $Fixture)) { return }
    $link = P 'link'
    if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) }    # a junction: removes the link only
    [void](Invoke-Native cmd.exe @('/c', 'rd', '/s', '/q', $Fixture))
}

function New-Fixture {
    Remove-Fixture
    Make-Directory 'src/deep/deeper', 'src/dist', '.hiddendir', 'hiddenattrdir', 'node_modules/pkg', 'BIN', 'Docs', 'Program Files', 'empty'
    Touch 'Alpha.txt', 'alpine.TXT', 'beta.cs', 'Gamma.CS', 'noext', 'dot.in.name.tar.gz', "$cafe.txt", "$nihon.txt", 'with space.md',
          'src/main.cs', 'src/Util.cs', 'src/deep/deeper/x.cs', 'src/dist/z.js', '.hiddendir/f.txt', '.dotfile', 'hiddenattr.txt',
          'hiddenattrdir/inside.txt', 'node_modules/pkg/index.js', 'BIN/y.txt', 'Docs/readme.md', 'Program Files/a.txt'
    [IO.File]::WriteAllBytes((P 'Alpha.txt'), [Text.Encoding]::ASCII.GetBytes("hello`n"))
    [IO.File]::WriteAllBytes((P 'beta.cs'), [Text.Encoding]::ASCII.GetBytes('x'))
    [IO.File]::WriteAllBytes((P 'src/Util.cs'), (New-Object byte[] 5000))
    [IO.File]::WriteAllBytes((P 'Docs/readme.md'), (New-Object byte[] 1500))
    [IO.File]::SetAttributes((P 'hiddenattr.txt'), [IO.FileAttributes]::Hidden)
    [IO.File]::SetAttributes((P 'hiddenattrdir'), [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::Hidden)
    [void](Invoke-Native cmd.exe @('/c', 'mklink', '/J', (P 'link'), (P 'src')))
    [void](Invoke-Native cmd.exe @('/c', 'mklink', 'filelink', 'Alpha.txt') $Fixture)    # needs symlink privilege or developer mode
    Touch 'a.exe', 'b.bat', 'c.cmd', 'd.com', 'E.EXE', 'f.ps1'

    # a git repository with a spread of ignore rules
    Make-Directory 'repo/.git/info', 'repo/src/sub', 'repo/build', 'repo/docs/x', 'repo/a/cache', 'repo/b/cache', 'repo/dir2/dir-only', 'repo/logs/sub', 'repo/sub/deeper', 'repo/nested/.git'
    Write-Text 'repo/.git/info/exclude' 'excluded-by-info.txt'
    Write-Text 'repo/.gitignore' '*.log', 'build/', '/rooted.txt', 'src/gen*', '!keep.log', 'Foo.TXT', '*.tmp', '!important.tmp', 'docs/**/*.bak',
        '**/cache', 'temp[0-9].txt', '\#hash.txt', 'dir-only/', '/*.o', 'logs/**', '!logs/keep.txt', '.env', '!.env', '.hidden-ignored',
        '*.[ch]pp', '# comment', ''
    Write-Text 'repo/sub/.gitignore' '*.txt', '!keep.txt'
    Write-Text 'repo/.ignore' '*.ign'
    Write-Text 'repo/.fdignore' '!a.log', 'fd-only.txt'
    Write-Text 'repo/nested/.gitignore' '*.nested'
    Touch (@('a.log', 'keep.log', 'rooted.txt', 'sub/rooted.txt', 'src/genA.c', 'src/sub/genB.c', 'src/gen.c', 'build/x.o', 'foo.txt', 'Foo.TXT',
          'x.tmp', 'important.tmp', 'docs/a.bak', 'docs/x/y.bak', 'a/cache/z', 'b/cache/c.txt', 'temp1.txt', 'tempX.txt', '#hash.txt', 'dir-only',
          'dir2/dir-only/z.txt', 'main.o', 'sub/main.o', 'logs/a.txt', 'logs/keep.txt', 'logs/sub/b.txt', '.env', '.hidden-ignored',
          'excluded-by-info.txt', 'x.ign', 'fd-only.txt', 'c.cpp', 'c.hpp', 'c.cc', 'sub/a.txt', 'sub/keep.txt', 'sub/deeper/b.txt',
          'sub/deeper/b.md', 'nested/x.nested', 'nested/a.log', 'nested/n.txt') | ForEach-Object { "repo/$_" })

    # ignore files outside a repository
    Write-Text 'plain/.gitignore' '*.txt'
    Write-Text 'plain/.ignore' '*.md'
    Touch 'plain/a.txt', 'plain/b.md', 'plain/c.rs'
}

# ---- comparing ----

$pass = 0
$fail = 0
$script:Where = $null
$script:Env = $null
$script:Split = $false

function Format-Output([string]$Text, [bool]$Split = $false) {
    $parts = $Text.Replace('\', '/') -split "[`0`n]"
    if ($Split) { $parts = $Text.Replace('\', '/') -split '\s+' }
    $lines = [string[]]@($parts)
    if (-not $Split -and $lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') { $lines = [string[]]@($lines | Select-Object -First ($lines.Count - 1)) }
    $lines = [string[]]@($lines | Where-Object { -not $Split -or $_ -ne '' })
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    , $lines
}

function Test-Same([string[]]$A, [string[]]$B) {
    [string]::Equals([string]::Join("`n", $A), [string]::Join("`n", $B), [StringComparison]::Ordinal)
}

function Pass { $script:pass++ }

function Fail([string]$Message) {
    $script:fail++
    Write-Host "FAIL: $Message"
}

function Show-Difference([string[]]$Ours, [string[]]$Theirs) {
    $only = New-Object Collections.Generic.HashSet[string] (, $Ours)
    $only.ExceptWith($Theirs)
    $missing = New-Object Collections.Generic.HashSet[string] (, $Theirs)
    $missing.ExceptWith($Ours)
    $shown = @($only | ForEach-Object { "< $_" }) + @($missing | ForEach-Object { "> $_" })
    $shown | Select-Object -First 8 | ForEach-Object { Write-Host $_ }
}

# Check <name> <args...>: runs fd-cs and fd with the same arguments in the fixture (or $script:Where) and compares output and exit status.
# Arguments that PowerShell would read as numbers or operators (-0, 1d) have to be quoted.
function Check {
    $name = $args[0]
    $arguments = [string[]]@($args | Select-Object -Skip 1 | ForEach-Object { "$_" })
    $directory = if ($script:Where) { $script:Where } else { $Fixture }

    $ours = Invoke-Native $Exe (@('--no-skip') + $arguments) $directory $script:Env
    $theirs = Invoke-Native $RealFd $arguments $directory $script:Env
    $left = (Format-Output $ours.Out $script:Split)
    $right = (Format-Output $theirs.Out $script:Split)

    if ((Test-Same $left $right) -and $ours.Code -eq $theirs.Code) {
        Pass
        return
    }
    Fail "$name  (fd-cs rc $($ours.Code), fd rc $($theirs.Code))"
    Show-Difference $left $right
    if ($ours.Err) { $ours.Err -split "`r?`n" | Where-Object { $_ } | Select-Object -First 3 | ForEach-Object { Write-Host $_ } }
}

function With([hashtable]$Settings, [scriptblock]$Body) {
    $script:Where = $Settings.Where
    $script:Env = $Settings.Env
    $script:Split = [bool]$Settings.Split
    try { & $Body } finally { $script:Where = $null; $script:Env = $null; $script:Split = $false }
}

function Run-Ours([string[]]$Arguments, [string]$Directory = $Fixture) { Invoke-Native $Exe $Arguments $Directory }
function Run-Fd([string[]]$Arguments, [string]$Directory = $Fixture) { Invoke-Native $RealFd $Arguments $Directory }
function Get-Lines($Result) { , (Format-Output $Result.Out) }

New-Fixture

# matching
Check 'default'
Check '-H' -H
Check '-I' -I
Check '-u' -u
Check '-HI' -HI
Check 'regex prefix' '^a'
Check 'regex alternation' '\.(cs|txt)$'
Check 'plain substring' ta
Check 'smart case upper' Alpha
Check 'smart case lower' alpha
Check '-s' -s alpha
Check '-i' -i Alpha
Check '-s then -i' -s -i ALPHA
Check 'uppercase class' '[A-Z]'
Check 'uppercase escape' '\p{Lu}'
Check 'word class' '\w'
Check 'unicode' $cafe
Check 'unicode glob' -g "$nihon*"
Check '-F' -F 'tar.gz'
Check '-F metachar' -F '.in.'
Check '--exact' --exact alpine.txt
Check '--exact -s' --exact -s alpine.TXT
Check '--and' --and txt --and al
Check '--and glob' -g --and '*.txt' 'a*'
Check '-g suffix' -g '*.txt'
Check '-g prefix' -g 'al*'
Check '-g question' -g '?lpha.txt'
Check '-g exact' -g noext
Check '-g whole name' -g 'lpha'
Check '-g middle star' -g 'a*a.txt'
Check '-g class' -g '[a-b]*'
Check '-g negated class' -g '[!a-b]*.txt'
Check '-g alternation' -g '*.{cs,md}'
Check '-g double star' -g '**.txt'
Check '-g backslash' -g 'with\ space*'
Check '-g case' -g -s 'Alpha*'
Check '-p' -p 'src.deep'
Check '-p regex' -p '\\src\\'
Check '-p glob' -g -p '**/*.cs'
Check '-p anchored' -p '^[a-z]:'
Check '-a' -a main
Check '-a -p' -a -p 'repo.sub'
Check 'empty pattern' ''
Check 'dot pattern' '.'

# types and filters
Check '-t f' -t f
Check '-t d' -t d
Check '-t l' -t l
Check '-t x' -t x
Check '-t x -H' -t x -H
Check '-t e' -t e
Check '-t e -t f' -t e -t f
Check '-t e -t d' -t e -t d
Check '-t f -t d' -t f -t d
Check '-t l -t f' -t l -t f
Check '-t s' -t s
Check '-e' -e cs
Check '-e repeated' -e cs -e md
Check '-e dotted' -e '.cs'
Check '-e multi-dot' -e tar.gz
Check '-e empty' -e ''
Check '-e with -t d' -e cs -t d
Check '-e and pattern' -e txt al
Check '-d 0' -d '0'
Check '-d 1' -d '1'
Check '-d 2' -d '2'
Check '--min-depth 2' --min-depth '2'
Check '--exact-depth 2' --exact-depth '2'
Check '--min-depth --max-depth' --min-depth '2' -d '3'
Check '-S +1k' -S '+1k'
Check '-S -1k' -S '-1k'
Check '-S exact' -S '5000b'
Check '-S ki' -S '+1ki'
Check '-S two' -S '+1b' -S '-2k'
Check '-S with -t d' -S '+1b' -t d
Check '--changed-within 1d' --changed-within '1d'
Check '--changed-within 1 day' --changed-within '1 day'
Check '--changed-before 1d' --changed-before '1d'
Check '--changed-before date' --changed-before '2000-01-01'
Check '--changed-within date' --changed-within '2000-01-01'
Check '--changed-within datetime' --changed-within '2000-01-01 10:00:00'
Check '--changed-within timestamp' --changed-within '@946684800'
Check '--newer alias' --newer '1d'
Check '-E name' -E src
Check '-E glob' -E '*.cs'
Check '-E two' -E src -E '*.txt'
Check '-E anchored' -E 'src/deep'
Check '-E rooted' -E '/Docs'
Check '-E dir' -E 'deep*'
Check '--prune' --prune deep
Check '--prune -t d' --prune -t d s
Check '-L' -L
Check '-L -t f' -L -t f
Check '-L -t l' -L -t l
Check '-L -d 2' -L -d '2'

# roots
Check 'root argument' -e cs '' src
Check 'root argument, pattern' main src
Check 'root with trailing slash' -e cs '' 'src/'
Check 'root dot' -e cs '' '.'
Check 'root dot slash' -e cs '' './'
Check 'two roots' -e cs '' src Docs
Check '--search-path' -e cs --search-path src
Check 'root absolute' -e cs '' "$Fixture/src"
Check 'root missing' x no-such-dir
Check 'root missing and valid' -e cs '' no-such-dir src
Check '-C' -e cs -C src
Check '-C missing' -C no-such-dir

# ignore files
Check 'repo' '' repo
Check 'repo -H' -H '' repo
Check 'repo -I' -I '' repo
Check 'repo -u' -u '' repo
Check 'repo --no-ignore-vcs' --no-ignore-vcs '' repo
Check 'repo --no-require-git' --no-require-git '' repo
Check 'repo --no-ignore-parent' --no-ignore-parent '' repo
Check 'repo subdir' '' repo/sub
Check 'repo subdir --no-ignore-parent' --no-ignore-parent '' repo/sub
Check 'repo subdir --no-require-git' --no-require-git '' repo/sub
Check 'repo src' '' repo/src
Check 'repo nested' '' repo/nested
Check 'repo nested -t f' -t f '' repo/nested
Check 'plain' '' plain
Check 'plain --no-require-git' --no-require-git '' plain
Check 'plain -I' -I '' plain
Check 'from fixture, repo files' -t f txt
Check 'ignore dir only' '' repo/dir2
Check '--ignore-file' --ignore-file "$Fixture/plain/.gitignore" '' src
Check '--ignore-file two' --ignore-file "$Fixture/plain/.gitignore" --ignore-file "$Fixture/repo/.ignore" '' repo
Check '--ignore-file with -I' -I --ignore-file "$Fixture/plain/.gitignore" '' plain
With @{ Where = (P 'repo') } { Check 'cwd is repo' '' }
With @{ Where = (P 'repo/sub') } { Check 'cwd is repo/sub' '' }
With @{ Where = (P 'repo/src/sub') } { Check 'cwd is repo/src/sub' '' }
With @{ Where = (P 'plain') } { Check 'cwd is plain' '' }
With @{ Where = (P 'plain') } { Check 'cwd is plain, --no-require-git' --no-require-git '' }
Check 'ignore-contain' --ignore-contain .git
Check 'ignore-contain -H' --ignore-contain .git -H
Check 'ignore-contain two' --ignore-contain .git --ignore-contain .ignore -H

# output
Check '-0' '-0' -e cs
Check '--format' --format '{/} in {//} ({.}, {/.})' -e cs
Check '--format escapes' --format '{{}}{}' -e cs src
Check '--path-separator' --path-separator '#' -e cs
Check '--strip-cwd-prefix' --strip-cwd-prefix -e cs
Check '--strip-cwd-prefix=never' --strip-cwd-prefix=never -e cs
Check '--color never' --color never -e cs
Check '--color always' --color always
Check '--color always -a' --color always -a -e cs
Check '--color always, root' --color always '' src
Check '--color always, dot root' --color always '' './src'
With @{ Env = @{ LS_COLORS = 'di=1;31:*.md=4;32:*.CS=0;35:fi=0;33:ex=1' } } { Check '--color always, LS_COLORS' --color always }
With @{ Env = @{ LS_COLORS = '' } } { Check '--color always, empty LS_COLORS' --color always }
Check '--color always --format' --color always --format '{/}'
Check '--hyperlink' --hyperlink=always -e cs
Check '--hyperlink and color' --hyperlink=always --color always -e cs src
Check '--hyperlink auto' --hyperlink -e cs

# cmd.exe stands in for the Unix echo and false, which Windows does not have
Check '-x' -e cs -x cmd /c echo
Check '-x placeholders' -e cs -x cmd /c echo '{/}' '{//}' '{.}' '{/.}'
Check '-x two commands' -e cs -x cmd /c echo a '{}' ';' -x cmd /c echo b '{/}'
Check '-x -0' '-0' -e cs -x cmd /c echo
Check '-x -j 1' -j 1 -e cs -x cmd /c echo
Check '-x failing' -e cs -x cmd /c exit 1
Check '-x missing command' -e cs -x no-such-command-here
With @{ Split = $true } { Check '-X' -e cs -X cmd /c echo }
With @{ Split = $true } { Check '-X placeholder' -e cs -X cmd /c echo 'x{/}' tail }
Check '-X batch-size' -e cs --batch-size 1 -X cmd /c echo
Check '-X no results' zzzzqqqq -X cmd /c echo
Check '-l' -e cs -l

# exit status
Check '-q match' -q alpha
Check '-q no match' -q zzzzqqqq
Check 'no match' zzzzqqqq
Check 'invalid regex' '('
Check 'pattern looks like a path' 'src/'
Check 'unknown option' --bogus
Check 'missing value' -d
Check 'bad number' -d x
Check 'bad type' -t q
Check 'bad size' -S '12x'
Check 'bad date' --changed-within notadate
Check '-q with --max-results' -q --max-results 1
Check '--exact with -g' --exact -g x

$count = (Get-Lines (Run-Ours @('--max-results', '3', '-H', '--no-skip'))).Count
if ($count -eq 3) { Pass } else { Fail "--max-results 3 printed $count" }
$count = (Get-Lines (Run-Ours @('--max-results', '0', '-H', '-I', '--no-skip'))).Count
$total = (Get-Lines (Run-Fd @('-H', '-I'))).Count
if ($count -ge $total) { Pass } else { Fail "--max-results 0 printed $count of $total" }

$one = Get-Lines (Run-Ours @('--no-skip', '-1', '-e', 'cs'))
$all = Get-Lines (Run-Fd @('-e', 'cs'))
if ($one.Count -eq 1 -and $all -ccontains $one[0]) { Pass } else { Fail "-1 printed: $($one -join ' ')" }

# the skip list: not part of fd, so checked against fixed expectations
$out = (Get-Lines (Run-Ours @('-t', 'f', ''))) -join ' '
if ($out -cmatch 'node_modules|Program Files|src/dist|BIN/') { Fail "skip list not applied: $out" } else { Pass }
$out = (Get-Lines (Run-Ours @('-t', 'f', '--no-skip', 'index'))) -join ' '
if ($out -ceq 'node_modules/pkg/index.js') { Pass } else { Fail "--no-skip: $out" }
$out = (Get-Lines (Run-Ours @('-u', '-t', 'f', 'index'))) -join ' '
if ($out -ceq 'node_modules/pkg/index.js') { Pass } else { Fail "-u implies --no-skip: $out" }
Touch 'build', 'dist/inner.txt'
$out = (Get-Lines (Run-Ours @('-t', 'f', 'build'))) -join ' '
if ($out -ceq 'build') { Pass } else { Fail "file named build: $out" }
$out = (Get-Lines (Run-Ours @('-t', 'f', 'inner') (P 'dist'))) -join ' '
if ($out -ceq 'inner.txt') { Pass } else { Fail "root named dist: $out" }

if ($BigRoot) {
    $specs = @{
        'full crawl'         = @('-H', '-I', 'e')
        'default crawl'      = @('e')
        'ignore-aware crawl' = @('-H', 'e')
        'files only'         = @('-t', 'f', '-e', 'rs')
    }
    foreach ($label in $specs.Keys) {
        $arguments = [string[]]$specs[$label]
        $left = Get-Lines (Run-Ours (@('--no-skip') + $arguments + $BigRoot))
        $right = Get-Lines (Run-Fd ($arguments + $BigRoot))
        if (Test-Same $left $right) { Pass } else { Fail "$label ($($left.Count) vs $($right.Count) lines)"; Show-Difference $left $right }
    }
    $count = (Get-Lines (Run-Ours @('-j', '8', '--max-results', '50', '-H', '--no-skip', 'e', $BigRoot))).Count
    if ($count -eq 50) { Pass } else { Fail "--max-results 50 with 8 threads printed $count" }
}

Remove-Fixture
Write-Host "$pass passed, $fail failed"
exit ([int]($fail -ne 0))
