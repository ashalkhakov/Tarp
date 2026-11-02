#!/usr/bin/env bash
# End-to-end check of the tool as users get it: packs Tarp, installs it as a local tool into a
# copy of samples/Shop, runs `dotnet tarp test`, and checks the reports against known numbers.
# With GENHTML set (lcov 2.x's genhtml), also checks that lcov reads the tracefile.
#
#   samples/e2e.sh [nupkg directory]   (default: pack the working tree)
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

if [ $# -ge 1 ]; then
    nupkg=$(cd "$1" && pwd)
else
    nupkg="$work/nupkg"
    # a version of its own: NuGet would install a package of the same version from its cache
    dotnet pack "$repo/Tarp.BuildTool" -c Release -o "$nupkg" -p:Version=0.0.0-e2e.$(date +%s)
fi
version=$(ls "$nupkg" | sed -n 's/^dotnet-tarp\.\(.*\)\.nupkg$/\1/p' | head -1)

cp -R "$repo/samples/Shop" "$work/Shop"
cd "$work/Shop"

dotnet new tool-manifest
dotnet tool install dotnet-tarp --add-source "$nupkg" --version "$version"

dotnet tarp test --tarp-report report.txt --tarp-lcov coverage.info --tarp-cobertura coverage/cobertura.xml --tarp-markdown report.md --tarp-html report.html
cat report.txt

# line, branch, function and condition coverage
grep -E "^  Lines +10/10 " report.txt
grep -E "^  Branches +9/10 " report.txt
grep -E "^  Functions +4/4 .*exits 7/7" report.txt
grep -E "^  Conditions +6/11 " report.txt
grep -F "line 24: o is null was only true" report.txt
# hit counts: CanShip's if ran once per call (three calls)
grep -F "most run: line 12 (3 times)" report.txt
# MC/DC
grep -F "Summary: 5 decisions (0 never executed), 5/11 conditions shown (45.5%), 0 decisions not instrumented" report.txt
grep -F "c0 paid: NOT shown, missing false" report.txt
grep -F "try: c0=F c1=- c2=F" report.txt
# a decision with a pattern variable, used in the condition after it
grep -F "c0 o is string s: shown" report.txt
grep -F "c1 s.Length > 0: NOT shown, missing false" report.txt

# the lcov tracefile: the same numbers, MC/DC per condition and sense
# SF: the source's full path, so that lcov tools find it
grep -E '^SF:([A-Za-z]:)?[/\\].*Shop\.Core[/\\]Orders\.cs$' coverage.info
for record in LF:10 LH:10 BRF:10 BRH:9 FNF:4 FNH:4 MCF:22 MCH:16 DA:12,3 "MCDC:12,3,f,0,0,paid"; do
    grep -Fx "$record" coverage.info
done
# Cobertura: lines and branches, file names relative to the sources' directory
grep -F 'lines-covered="10" lines-valid="10" branches-covered="9" branches-valid="10"' coverage/cobertura.xml
grep -F '<package name="Shop.Core"' coverage/cobertura.xml
grep -F '<class name="Shop.Core.Orders" filename="Orders.cs"' coverage/cobertura.xml
grep -F '<method name="CanShip" signature="(bool, bool, bool)" line-rate="1" branch-rate="1"' coverage/cobertura.xml
grep -E '<line number="12" hits="3" branch="true" condition-coverage="[0-9]+% \([0-9]+/[0-9]+\)" */>' coverage/cobertura.xml
# Markdown and HTML: the summary, and MC/DC as tables
grep -F '| MC/DC | 5 | 11 | 45.5% |' report.md
grep -F '| c0 | `paid` | ✓ | ✗ | **not shown** | `c0=F c1=- c2=F` |' report.md
grep -F '<td class="gap">not shown</td><td><code>c0=F c1=- c2=F</code></td>' report.html
if [ -n "${GENHTML:-}" ]; then
    "$GENHTML" --branch-coverage --mcdc-coverage coverage.info -o html 2>&1 | tee genhtml.txt
    grep -E "lines\.*: 100\.0% \(10 of 10 lines\)" genhtml.txt
    grep -E "branches\.*: 90\.0% \(9 of 10 branches\)" genhtml.txt
    grep -E "functions\.*: 100\.0% \(4 of 4 functions\)" genhtml.txt
    grep -E "MC/DCs\.*: 72\.7% \(16 of 22 MC/DCs\)" genhtml.txt
fi

# a normal build afterwards compiles the original sources again
dotnet build
if grep -a -q TarpGen Shop.Core/bin/Debug/net10.0/Shop.Core.dll; then
    echo "error: a normal build after dotnet tarp test is still instrumented" >&2
    exit 1
fi

echo "e2e: OK"
