#!/usr/bin/env bash
# Packs the hub client (Lemon.Hub.Client, .Desktop and .Browser) and the shell (Lemon.Book.Shell, .Desktop and
# .Browser) into artifacts/packages, and into an app's local package folder when one is given:
#
#   <app>/packages/Lemon.Hub.Client.<version>.nupkg (and .Desktop, .Browser)
#   <app>/packages/Lemon.Book.Shell.<version>.nupkg (and .Desktop, .Browser)
#
# Usage: scripts/pack.sh [app]
#   app  the book's (or other app's) repository (its NuGet.config lists packages/ before nuget.org). A book needs
#        both families (the shell depends on the hub client); an app that is not a book only uses Lemon.Hub.Client*.
#
# Bump <Version> in Directory.Build.props first: NuGet caches a package by its version, so an app would keep the old
# one. scripts/push-nuget.sh packs and publishes to nuget.org.
set -euo pipefail

cd "$(dirname "$0")/.."

BOOK="${1:-}"
OUT=artifacts/packages
VERSION="$(sed -nE 's|.*<Version>([^<]+)</Version>.*|\1|p' Directory.Build.props | head -1)"
# The hub client first: the shell's packages depend on it.
PROJECTS=(Lemon.Hub.Client Lemon.Hub.Client.Desktop Lemon.Hub.Client.Browser Lemon.Book.Shell Lemon.Book.Shell.Desktop Lemon.Book.Shell.Browser)

if [[ -n "$BOOK" && ! -d "$BOOK" ]]; then
    echo "No app at $BOOK; pass its folder." >&2
    exit 2
fi
if [[ -n "$BOOK" && -f "$BOOK/packages/Lemon.Book.Shell.$VERSION.nupkg" ]]; then
    echo "Lemon.Book.Shell.$VERSION is already in $BOOK/packages: bump <Version> in Directory.Build.props." >&2
    exit 1
fi

dotnet test tests/Lemon.Hub.Client.Tests
dotnet test tests/Lemon.Book.Shell.Tests
for project in "${PROJECTS[@]}"; do
    dotnet pack "src/$project" -c Release -o "$OUT"
done

# The web package must carry its scripts: a book serves them at _content/Lemon.Book.Shell.Browser/.
for script in narration.js download-progress.js wake-lock.js; do
    if ! unzip -l "$OUT/Lemon.Book.Shell.Browser.$VERSION.nupkg" | grep -q "staticwebassets/$script"; then
        echo "Lemon.Book.Shell.Browser.$VERSION.nupkg has no $script." >&2
        exit 1
    fi
done

# A book's font subsetting reads the shell's strings from strings/ in the package.
for strings in ShellStrings.resx ShellStrings.en.resx; do
    if ! unzip -l "$OUT/Lemon.Book.Shell.$VERSION.nupkg" | grep -q "strings/$strings"; then
        echo "Lemon.Book.Shell.$VERSION.nupkg has no strings/$strings." >&2
        exit 1
    fi
done

# The books name the sign-in types under their old namespaces: the shell's packages bring the usings that keep that
# compiling (buildTransitive/).
for usings in Lemon.Book.Shell/Lemon.Book.Shell.props Lemon.Book.Shell.Browser/Lemon.Book.Shell.Browser.targets; do
    package="${usings%%/*}"
    if ! unzip -l "$OUT/$package.$VERSION.nupkg" | grep -q "buildTransitive/${usings#*/}"; then
        echo "$package.$VERSION.nupkg has no buildTransitive/${usings#*/}." >&2
        exit 1
    fi
done

# Each hub client package carries the hub client's README, not the shell's.
for package in Lemon.Hub.Client Lemon.Hub.Client.Desktop Lemon.Hub.Client.Browser; do
    if ! unzip -p "$OUT/$package.$VERSION.nupkg" README.md | grep -q "^# Lemon.Hub.Client"; then
        echo "$package.$VERSION.nupkg does not carry the hub client's README." >&2
        exit 1
    fi
done

if [[ -n "$BOOK" ]]; then
    mkdir -p "$BOOK/packages"
    for project in "${PROJECTS[@]}"; do
        cp "$OUT/$project.$VERSION.nupkg" "$BOOK/packages/"
    done
    echo "Copied version $VERSION to $BOOK/packages."
fi
