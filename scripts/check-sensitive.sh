#!/usr/bin/env bash
# Fails when tracked (or staged) files contain something that must not reach this public repository.
#
#   scripts/check-sensitive.sh            scan every tracked file
#   scripts/check-sensitive.sh --staged   scan only what is staged (the pre-commit hook uses this)
#
# Built-in patterns catch generic secrets. Project-specific strings (private host names, internal product names,
# e-mail addresses) are deliberately NOT written here, since listing them would publish them. Put one regular
# expression per line in either:
#   - .sensitive-patterns at the repository root (git-ignored, local only), or
#   - the SENSITIVE_PATTERNS environment variable (newline separated; a CI secret).
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

mode="${1:-all}"
if [[ "$mode" == "--staged" ]]; then
    mapfile -t files < <(git diff --cached --name-only --diff-filter=ACMR)
else
    mapfile -t files < <(git ls-files)
fi

patterns=(
    '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    'AKIA[0-9A-Z]{16}'
    'gh[pousr]_[A-Za-z0-9]{36,}'
    'github_pat_[A-Za-z0-9_]{40,}'
    'sk-[A-Za-z0-9_-]{32,}'
    'xox[abprs]-[A-Za-z0-9-]{10,}'
    'AccountKey=[A-Za-z0-9+/=]{20,}'
    'SharedAccessSignature=|sig=[A-Za-z0-9%]{30,}'
    'eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.'
    '(password|passwd|pwd|secret|api[_-]?key|client[_-]?secret)["'"'"' ]*[:=] *["'"'"'][^"'"'"' ]{6,}'
)

if [[ -f .sensitive-patterns ]]; then
    while IFS= read -r line; do
        [[ -n "$line" && "$line" != \#* ]] && patterns+=("$line")
    done < .sensitive-patterns
fi

if [[ -n "${SENSITIVE_PATTERNS:-}" ]]; then
    while IFS= read -r line; do
        [[ -n "$line" ]] && patterns+=("$line")
    done <<< "$SENSITIVE_PATTERNS"
fi

found=0
for file in "${files[@]}"; do
    # This script's own pattern list would match itself.
    [[ -f "$file" && "$file" != "scripts/check-sensitive.sh" ]] || continue
    # Binary files (fonts, images) are skipped; -I makes grep ignore them.
    for pattern in "${patterns[@]}"; do
        if grep -I -n -i -E -q -- "$pattern" "$file"; then
            # Print where, never what: the match itself may be the secret.
            grep -I -n -i -E -- "$pattern" "$file" | cut -d: -f1 | sed "s|^|  $file:|" >&2
            found=1
        fi
    done
done

if [[ "$found" -ne 0 ]]; then
    echo "Sensitive content found at the lines above. Remove it before committing." >&2
    exit 1
fi

echo "No sensitive content found in ${#files[@]} files."
