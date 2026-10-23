#!/bin/bash
# Compares fq with the real fd on a generated fixture tree: same arguments, sorted output, exit codes.
# usage: check.sh <fq-exe> [fixture-dir]      set BIG_ROOT=D:/ to also compare full-drive walks
# fq runs with --no-skip, because fd has no skip list.
export MSYS2_ARG_CONV_EXCL='*'
command -v fd >/dev/null || { echo "fd not found in PATH"; exit 2; }
exe=${1:?usage: check.sh <fq-exe> [fixture-dir]}
exe=$(realpath "$exe")
fixture=${2:-${TMPDIR:-/tmp}/fq-fixture}
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT

mk() { mkdir -p "$(dirname "$1")"; printf '%s' "${2:-}" > "$1"; }

make_fixture() {
    rm -rf "$fixture" && mkdir -p "$fixture" && cd "$fixture" || exit 1
    mkdir -p src/deep/deeper src/dist .hiddendir hiddenattrdir node_modules/pkg BIN Docs 'Program Files' empty
    touch Alpha.txt alpine.TXT beta.cs Gamma.CS noext dot.in.name.tar.gz 'café.txt' '日本.txt' 'with space.md' \
          src/main.cs src/Util.cs src/deep/deeper/x.cs src/dist/z.js .hiddendir/f.txt .dotfile hiddenattr.txt \
          hiddenattrdir/inside.txt node_modules/pkg/index.js BIN/y.txt Docs/readme.md 'Program Files/a.txt'
    echo hello > Alpha.txt; printf 'x' > beta.cs; head -c 5000 /dev/zero > src/Util.cs; head -c 1500 /dev/zero > Docs/readme.md
    attrib +h hiddenattr.txt
    attrib +h hiddenattrdir
    cmd /c mklink /J link "$(cygpath -w "$PWD/src")" >/dev/null
    cmd /c mklink filelink Alpha.txt >/dev/null 2>&1 || true    # needs symlink privilege or developer mode
    touch a.exe b.bat c.cmd d.com E.EXE f.ps1

    # a git repository with a spread of ignore rules
    mkdir -p repo/.git/info repo/src/sub repo/build repo/docs/x repo/a/cache repo/b/cache repo/dir2/dir-only repo/logs/sub repo/sub/deeper repo/nested/.git
    mk repo/.git/info/exclude $'excluded-by-info.txt\n'
    mk repo/.gitignore $'*.log\nbuild/\n/rooted.txt\nsrc/gen*\n!keep.log\nFoo.TXT\n*.tmp\n!important.tmp\ndocs/**/*.bak\n**/cache\ntemp[0-9].txt\n\\#hash.txt\ndir-only/\n/*.o\nlogs/**\n!logs/keep.txt\n.env\n!.env\n.hidden-ignored\n*.[ch]pp\n# comment\n\n'
    mk repo/sub/.gitignore $'*.txt\n!keep.txt\n'
    mk repo/.ignore $'*.ign\n'
    mk repo/.fdignore $'!a.log\nfd-only.txt\n'
    mk repo/nested/.gitignore $'*.nested\n'
    for f in a.log keep.log rooted.txt sub/rooted.txt src/genA.c src/sub/genB.c src/gen.c build/x.o foo.txt Foo.TXT x.tmp important.tmp \
             docs/a.bak docs/x/y.bak a/cache/z b/cache/c.txt temp1.txt tempX.txt '#hash.txt' dir-only dir2/dir-only/z.txt main.o sub/main.o \
             logs/a.txt logs/keep.txt logs/sub/b.txt .env .hidden-ignored excluded-by-info.txt x.ign fd-only.txt c.cpp c.hpp c.cc \
             sub/a.txt sub/keep.txt sub/deeper/b.txt sub/deeper/b.md nested/x.nested nested/a.log nested/n.txt; do
        touch "repo/$f"
    done
    # ignore files outside a repository
    mkdir -p plain; mk plain/.gitignore $'*.txt\n'; mk plain/.ignore $'*.md\n'; touch plain/a.txt plain/b.md plain/c.rs
    cd - >/dev/null
}

norm() { tr '\\' '/' | tr '\0' '\n' | LC_ALL=C sort; }
post() { if [ -n "${SPLIT:-}" ]; then tr ' ' '\n' | LC_ALL=C sort; else cat; fi; }
pass=0
fail=0
ok() { pass=$((pass + 1)); }
bad() { fail=$((fail + 1)); echo "FAIL: $1"; }

# check <name> <args...>: run fq and fd with the same arguments in the fixture (or $WHERE) and compare output and exit status
check() {
    local name=$1
    shift
    (cd "${WHERE:-$fixture}" && "$exe" --no-skip "$@" 2>"$scratch/ea" | norm | post >"$scratch/a"; echo "${PIPESTATUS[0]}" >"$scratch/ra")
    (cd "${WHERE:-$fixture}" && fd "$@" 2>"$scratch/eb" | norm | post >"$scratch/b"; echo "${PIPESTATUS[0]}" >"$scratch/rb")
    if cmp -s "$scratch/a" "$scratch/b" && cmp -s "$scratch/ra" "$scratch/rb"; then
        ok
    else
        bad "$name  (fq rc $(cat "$scratch/ra"), fd rc $(cat "$scratch/rb"))"
        diff "$scratch/a" "$scratch/b" | head -${DIFF_LINES:-8}
        [ -s "$scratch/ea" ] && head -3 "$scratch/ea"
    fi
}

make_fixture

# matching
check "default"
check "-H" -H
check "-I" -I
check "-u" -u
check "-HI" -HI
check "regex prefix" '^a'
check "regex alternation" '\.(cs|txt)$'
check "plain substring" ta
check "smart case upper" Alpha
check "smart case lower" alpha
check "-s" -s alpha
check "-i" -i Alpha
check "-s then -i" -s -i ALPHA
check "uppercase class" '[A-Z]'
check "uppercase escape" '\p{Lu}'
check "word class" '\w'
check "unicode" 'café'
check "unicode glob" -g '日本*'
check "-F" -F 'tar.gz'
check "-F metachar" -F '.in.'
check "--exact" --exact alpine.txt
check "--exact -s" --exact -s alpine.TXT
check "--and" --and txt --and al
check "--and glob" -g --and '*.txt' 'a*'
check "-g suffix" -g '*.txt'
check "-g prefix" -g 'al*'
check "-g question" -g '?lpha.txt'
check "-g exact" -g noext
check "-g whole name" -g 'lpha'
check "-g middle star" -g 'a*a.txt'
check "-g class" -g '[a-b]*'
check "-g negated class" -g '[!a-b]*.txt'
check "-g alternation" -g '*.{cs,md}'
check "-g double star" -g '**.txt'
check "-g backslash" -g 'with\ space*'
check "-g case" -g -s 'Alpha*'
check "-p" -p 'src.deep'
check "-p regex" -p '\\src\\'
check "-p glob" -g -p '**/*.cs'
check "-p anchored" -p '^[a-z]:'
check "-a" -a main
check "-a -p" -a -p 'repo.sub'
check "empty pattern" ''
check "dot pattern" .

# types and filters
check "-t f" -t f
check "-t d" -t d
check "-t l" -t l
check "-t x" -t x
check "-t x -H" -t x -H
check "-t e" -t e
check "-t e -t f" -t e -t f
check "-t e -t d" -t e -t d
check "-t f -t d" -t f -t d
check "-t l -t f" -t l -t f
check "-t s" -t s
check "-e" -e cs
check "-e repeated" -e cs -e md
check "-e dotted" -e .cs
check "-e multi-dot" -e tar.gz
check "-e empty" -e ''
check "-e with -t d" -e cs -t d
check "-e and pattern" -e txt al
check "-d 0" -d 0
check "-d 1" -d 1
check "-d 2" -d 2
check "--min-depth 2" --min-depth 2
check "--exact-depth 2" --exact-depth 2
check "--min-depth --max-depth" --min-depth 2 -d 3
check "-S +1k" -S +1k
check "-S -1k" -S -1k
check "-S exact" -S 5000b
check "-S ki" -S +1ki
check "-S two" -S +1b -S -2k
check "-S with -t d" -S +1b -t d
check "--changed-within 1d" --changed-within 1d
check "--changed-within 1 day" --changed-within '1 day'
check "--changed-before 1d" --changed-before 1d
check "--changed-before date" --changed-before 2000-01-01
check "--changed-within date" --changed-within 2000-01-01
check "--changed-within datetime" --changed-within '2000-01-01 10:00:00'
check "--changed-within timestamp" --changed-within @946684800
check "--newer alias" --newer 1d
check "-E name" -E src
check "-E glob" -E '*.cs'
check "-E two" -E src -E '*.txt'
check "-E anchored" -E 'src/deep'
check "-E rooted" -E '/Docs'
check "-E dir" -E 'deep*'
check "--prune" --prune deep
check "--prune -t d" --prune -t d s
check "-L" -L
check "-L -t f" -L -t f
check "-L -t l" -L -t l
check "-L -d 2" -L -d 2

# roots
check "root argument" -e cs '' src
check "root argument, pattern" main src
check "root with trailing slash" -e cs '' src/
check "root dot" -e cs '' .
check "root dot slash" -e cs '' ./
check "two roots" -e cs '' src Docs
check "--search-path" -e cs --search-path src
check "root absolute" -e cs '' "$fixture/src"
check "root missing" x no-such-dir
check "root missing and valid" -e cs '' no-such-dir src
check "-C" -e cs -C src
check "-C missing" -C no-such-dir

# ignore files
check "repo" '' repo
check "repo -H" -H '' repo
check "repo -I" -I '' repo
check "repo -u" -u '' repo
check "repo --no-ignore-vcs" --no-ignore-vcs '' repo
check "repo --no-require-git" --no-require-git '' repo
check "repo --no-ignore-parent" --no-ignore-parent '' repo
check "repo subdir" '' repo/sub
check "repo subdir --no-ignore-parent" --no-ignore-parent '' repo/sub
check "repo subdir --no-require-git" --no-require-git '' repo/sub
check "repo src" '' repo/src
check "repo nested" '' repo/nested
check "repo nested -t f" -t f '' repo/nested
check "plain" '' plain
check "plain --no-require-git" --no-require-git '' plain
check "plain -I" -I '' plain
check "from fixture, repo files" -t f txt
check "ignore dir only" '' repo/dir2
check "--ignore-file" --ignore-file "$fixture/plain/.gitignore" '' src
check "--ignore-file two" --ignore-file "$fixture/plain/.gitignore" --ignore-file "$fixture/repo/.ignore" '' repo
check "--ignore-file with -I" -I --ignore-file "$fixture/plain/.gitignore" '' plain
WHERE=$fixture/repo check "cwd is repo" ''
WHERE=$fixture/repo/sub check "cwd is repo/sub" ''
WHERE=$fixture/repo/src/sub check "cwd is repo/src/sub" ''
WHERE=$fixture/plain check "cwd is plain" ''
WHERE=$fixture/plain check "cwd is plain, --no-require-git" --no-require-git ''
check "ignore-contain" --ignore-contain .git
check "ignore-contain -H" --ignore-contain .git -H
check "ignore-contain two" --ignore-contain .git --ignore-contain .ignore -H

# output
check "-0" -0 -e cs
check "--format" --format '{/} in {//} ({.}, {/.})' -e cs
check "--format escapes" --format '{{}}{}' -e cs src
check "--path-separator" --path-separator '#' -e cs
check "--strip-cwd-prefix" --strip-cwd-prefix -e cs
check "--strip-cwd-prefix=never" --strip-cwd-prefix=never -e cs
check "--color never" --color never -e cs
check "--color always" --color always
check "--color always -a" --color always -a -e cs
check "--color always, root" --color always '' src
check "--color always, dot root" --color always '' ./src
LS_COLORS='di=1;31:*.md=4;32:*.CS=0;35:fi=0;33:ex=1' check "--color always, LS_COLORS" --color always
LS_COLORS='' check "--color always, empty LS_COLORS" --color always
check "--color always --format" --color always --format '{/}'
check "--hyperlink" --hyperlink=always -e cs
check "--hyperlink and color" --hyperlink=always --color always -e cs src
check "--hyperlink auto" --hyperlink -e cs
check "-x" -e cs -x echo
check "-x placeholders" -e cs -x echo '{/}' '{//}' '{.}' '{/.}'
check "-x two commands" -e cs -x echo a '{}' ';' -x echo b '{/}'
check "-x -0" -0 -e cs -x echo
check "-x -j 1" -j 1 -e cs -x echo
check "-x failing" -e cs -x false
check "-x missing command" -e cs -x no-such-command-here
SPLIT=1 check "-X" -e cs -X echo
SPLIT=1 check "-X placeholder" -e cs -X echo 'x{/}' tail
check "-X batch-size" -e cs --batch-size 1 -X echo
check "-X no results" zzzzqqqq -X echo
check "-l" -e cs -l

# exit status
check "-q match" -q alpha
check "-q no match" -q zzzzqqqq
check "no match" zzzzqqqq
check "invalid regex" '('
check "pattern looks like a path" 'src/'
check "unknown option" --bogus
check "missing value" -d
check "bad number" -d x
check "bad type" -t q
check "bad size" -S 12x
check "bad date" --changed-within notadate
check "-q with --max-results" -q --max-results 1
check "--exact with -g" --exact -g x

count=$(cd "$fixture" && "$exe" --max-results 3 -H --no-skip | wc -l)
if [ "$count" -eq 3 ]; then ok; else bad "--max-results 3 printed $count"; fi
count=$(cd "$fixture" && "$exe" --max-results 0 -H -I --no-skip | wc -l)
total=$(cd "$fixture" && fd -H -I | wc -l)
if [ "$count" -ge "$total" ]; then ok; else bad "--max-results 0 printed $count of $total"; fi

out=$(cd "$fixture" && "$exe" --no-skip -1 -e cs)
if [ "$(echo "$out" | wc -l)" -eq 1 ] && (cd "$fixture" && fd -e cs | grep -qxF "$out"); then ok; else bad "-1 printed: $out"; fi

# the skip list: not part of fd, so checked against fixed expectations
out=$(cd "$fixture" && "$exe" -t f '' | norm | tr '\n' ' ')
case "$out" in *node_modules*|*"Program Files"*|*"src/dist"*|*BIN/*) bad "skip list not applied: $out" ;; *) ok ;; esac
out=$(cd "$fixture" && "$exe" -t f --no-skip index | norm)
if [ "$out" == "node_modules/pkg/index.js" ]; then ok; else bad "--no-skip: $out"; fi
out=$(cd "$fixture" && "$exe" -u -t f index | norm)
if [ "$out" == "node_modules/pkg/index.js" ]; then ok; else bad "-u implies --no-skip: $out"; fi
touch "$fixture/build" && mkdir -p "$fixture/dist" && touch "$fixture/dist/inner.txt"
out=$(cd "$fixture" && "$exe" -t f build | norm)
if [ "$out" == build ]; then ok; else bad "file named build: $out"; fi
out=$(cd "$fixture/dist" && "$exe" -t f inner | norm)
if [ "$out" == inner.txt ]; then ok; else bad "root named dist: $out"; fi

if [ -n "${BIG_ROOT:-}" ]; then
    cd "$fixture" || exit 1
    for spec in "full crawl|-H -I e" "default crawl|e" "ignore-aware crawl|-H e" "files only|-t f -e rs"; do
        IFS='|' read -r label args <<< "$spec"
        read -ra a <<< "$args"
        "$exe" --no-skip "${a[@]}" "$BIG_ROOT" 2>/dev/null | norm > "$scratch/a"
        fd "${a[@]}" "$BIG_ROOT" 2>/dev/null | norm > "$scratch/b"
        if cmp -s "$scratch/a" "$scratch/b"; then ok; else bad "$label ($(wc -l < "$scratch/a") vs $(wc -l < "$scratch/b") lines)"; diff "$scratch/a" "$scratch/b" | head -8; fi
    done
    count=$("$exe" -j 8 --max-results 50 -H --no-skip e "$BIG_ROOT" | wc -l)
    if [ "$count" -eq 50 ]; then ok; else bad "--max-results 50 with 8 threads printed $count"; fi
fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
