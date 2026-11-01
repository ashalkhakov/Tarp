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
    dotnet pack "$repo/Tarp.BuildTool" -c Release -o "$nupkg" -p:Version=0.0.0-e2e
fi
version=$(ls "$nupkg" | sed -n 's/^dotnet-tarp\.\(.*\)\.nupkg$/\1/p' | head -1)

cp -R "$repo/samples/Shop" "$work/Shop"
cd "$work/Shop"

dotnet new tool-manifest
dotnet tool install dotnet-tarp --add-source "$nupkg" --version "$version"

dotnet tarp test --tarp-report report.txt
cat report.txt

grep -F "Summary: 4 decisions (1 never executed), 4/9 conditions shown (44.4%), 1 decisions not instrumented" report.txt
grep -F "c0 paid: NOT shown, missing false" report.txt
grep -F "try: c0=F c1=- c2=F" report.txt
grep -F "o is string s && s.Length > 0  (declares pattern variables)" report.txt

# a normal build afterwards compiles the original sources again
dotnet build
if grep -a -q TarpGen Shop.Core/bin/Debug/net10.0/Shop.Core.dll; then
    echo "error: a normal build after dotnet tarp test is still instrumented" >&2
    exit 1
fi

echo "e2e: OK"
