#!/usr/bin/env bash
# Packs the shell (Lemon.Book.Shell, .Desktop and .Browser) into artifacts/packages, and into a book's local package
# folder when one is given:
#
#   <book>/packages/Lemon.Book.Shell.<version>.nupkg (and .Desktop, .Browser)
#
# Usage: scripts/pack.sh [book]
#   book  the book's repository (its NuGet.config lists packages/ before nuget.org)
#
# Bump <Version> in Directory.Build.props first: NuGet caches a package by its version, so a book would keep the old
# one. scripts/push-nuget.sh packs and publishes to nuget.org.
set -euo pipefail

cd "$(dirname "$0")/.."

BOOK="${1:-}"
OUT=artifacts/packages
VERSION="$(sed -nE 's|.*<Version>([^<]+)</Version>.*|\1|p' Directory.Build.props | head -1)"

if [[ -n "$BOOK" && ! -d "$BOOK" ]]; then
    echo "No book at $BOOK; pass its folder." >&2
    exit 2
fi
if [[ -n "$BOOK" && -f "$BOOK/packages/Lemon.Book.Shell.$VERSION.nupkg" ]]; then
    echo "Lemon.Book.Shell.$VERSION is already in $BOOK/packages: bump <Version> in Directory.Build.props." >&2
    exit 1
fi

dotnet test tests/Lemon.Book.Shell.Tests
for project in Lemon.Book.Shell Lemon.Book.Shell.Desktop Lemon.Book.Shell.Browser; do
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

if [[ -n "$BOOK" ]]; then
    mkdir -p "$BOOK/packages"
    cp "$OUT"/Lemon.Book.Shell*."$VERSION".nupkg "$BOOK/packages/"
    echo "Copied version $VERSION to $BOOK/packages."
fi
