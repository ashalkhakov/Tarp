#!/usr/bin/env bash
# End-to-end check of the tool as users get it: packs Tarp, installs it as a local tool into a
# copy of samples/Shop, runs `dotnet tarp test`, and checks the report against known numbers.
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

dotnet tarp test --tarp-report report.txt
cat report.txt

grep -F "Summary: 5 decisions (0 never executed), 5/11 conditions shown (45.5%), 0 decisions not instrumented" report.txt
grep -F "c0 paid: NOT shown, missing false" report.txt
grep -F "try: c0=F c1=- c2=F" report.txt
# a decision with a pattern variable, used in the condition after it
grep -F "c0 o is string s: shown" report.txt
grep -F "c1 s.Length > 0: NOT shown, missing false" report.txt

# a normal build afterwards compiles the original sources again
dotnet build
if grep -a -q TarpGen Shop.Core/bin/Debug/net10.0/Shop.Core.dll; then
    echo "error: a normal build after dotnet tarp test is still instrumented" >&2
    exit 1
fi

echo "e2e: OK"
