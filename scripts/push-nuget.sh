#!/usr/bin/env bash
# Packs the hub client (Lemon.Hub.Client, .Desktop, .Browser) and the shell (Lemon.Book.Shell, .Desktop, .Browser)
# and pushes the six packages to nuget.org.
#
# Usage: scripts/push-nuget.sh [--dry-run]
#   --dry-run  test and pack, say what would be pushed, push nothing
#
# The key is read from NUGET_API_KEY, never written anywhere. Bump <Version> in Directory.Build.props first: the six
# packages share it, and nuget.org never takes a version twice, so a version already there stops the script before
# anything is built. Packs only from a clean, committed tree, so every version on nuget.org is a commit here.
set -euo pipefail

cd "$(dirname "$0")/.."

DRY_RUN=
case "${1:-}" in
    --dry-run) DRY_RUN=1 ;;
    "") ;;
    *) echo "Usage: scripts/push-nuget.sh [--dry-run]" >&2; exit 2 ;;
esac

SOURCE=https://api.nuget.org/v3/index.json
OUT=artifacts/packages
VERSION="$(sed -nE 's|.*<Version>([^<]+)</Version>.*|\1|p' Directory.Build.props | head -1)"
# The hub client first, then the shell: each family's desktop and web packages depend on its first, and the shell
# depends on the hub client.
PACKAGES=(Lemon.Hub.Client Lemon.Hub.Client.Desktop Lemon.Hub.Client.Browser Lemon.Book.Shell Lemon.Book.Shell.Desktop Lemon.Book.Shell.Browser)

# On Windows, a shell started before the key was set does not have it yet: read the user's own.
if [[ -z "${NUGET_API_KEY:-}" ]] && command -v powershell.exe >/dev/null; then
    from_user="$(powershell.exe -NoProfile -Command "[Environment]::GetEnvironmentVariable('NUGET_API_KEY','User')")"
    NUGET_API_KEY=${from_user//[$'\r\n']/}
fi
if [[ -z "$DRY_RUN" && -z "${NUGET_API_KEY:-}" ]]; then
    echo "NUGET_API_KEY is not set." >&2
    exit 2
fi
if [[ -n "$(git status --porcelain)" ]]; then
    echo "Uncommitted changes: commit them first, so the packages are a commit." >&2
    exit 1
fi

# The versions nuget.org already has (lower case, as its flat container lists them; none yet for a new package).
for package in "${PACKAGES[@]}"; do
    id="$(tr '[:upper:]' '[:lower:]' <<<"$package")"
    if curl -fsS "https://api.nuget.org/v3-flatcontainer/$id/index.json" 2>/dev/null | grep -qi "\"$VERSION\""; then
        echo "$package $VERSION is already on nuget.org: bump <Version> in Directory.Build.props." >&2
        exit 1
    fi
done

# Nothing secret goes out in a package either.
scripts/check-sensitive.sh

# Tests, packs the six, and checks what each package must carry.
for package in "${PACKAGES[@]}"; do rm -f "$OUT/$package.$VERSION.nupkg"; done
scripts/pack.sh

if [[ -n "$DRY_RUN" ]]; then
    for package in "${PACKAGES[@]}"; do echo "Would push $OUT/$package.$VERSION.nupkg to $SOURCE."; done
    exit 0
fi

for package in "${PACKAGES[@]}"; do
    dotnet nuget push "$OUT/$package.$VERSION.nupkg" --source "$SOURCE" --api-key "$NUGET_API_KEY" --skip-duplicate
done
echo "Pushed ${PACKAGES[*]} $VERSION (nuget.org lists them after it has checked them, usually within minutes)."
echo "Then tag the commit: git tag v$VERSION && git push origin v$VERSION"
