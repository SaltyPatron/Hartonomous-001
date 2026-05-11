#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

ucd_root="$UCD_ROOT"
force=false

while (($#)); do
    case "$1" in
        --ucd-root)
            ucd_root="${2:?missing UCD root}"
            shift 2
            ;;
        --force)
            force=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/codegen-unicode.sh [--ucd-root PATH] [--force]
Regenerate embedded UCD/UCA extension tables from authoritative Unicode source files. This is offline codegen, not build or seed.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd python3

required_sources=(
    "$ucd_root/ucd/UnicodeData.txt"
    "$ucd_root/ucd/Blocks.txt"
    "$ucd_root/ucd/Scripts.txt"
    "$ucd_root/ucd/LineBreak.txt"
    "$ucd_root/ucd/CaseFolding.txt"
    "$ucd_root/ucd/DerivedCoreProperties.txt"
    "$ucd_root/ucd/auxiliary/GraphemeBreakProperty.txt"
    "$ucd_root/ucd/auxiliary/WordBreakProperty.txt"
    "$ucd_root/ucd/auxiliary/SentenceBreakProperty.txt"
    "$ucd_root/ucd/emoji/emoji-data.txt"
    "$ucd_root/uca/allkeys.txt"
)

missing=()
for file in "${required_sources[@]}"; do
    [[ -f "$file" ]] || missing+=("$file")
done

if ((${#missing[@]})); then
    printf 'Missing UCD source files:\n' >&2
    printf '  %s\n' "${missing[@]}" >&2
    die "set HARTONOMOUS_UCD_ROOT or pass --ucd-root to a complete UCD tree"
fi

if [[ "$force" == false ]] && generated_unicode_tables_present; then
    newest_source="$(find "${required_sources[@]}" -printf '%T@\n' | sort -nr | head -n 1)"
    oldest_generated="$(find ext/hartonomous_pg/src/generated -maxdepth 1 -type f \( -name '*.h' -o -name '*.c' \) -printf '%T@\n' | sort -n | head -n 1)"
    if awk "BEGIN { exit !($oldest_generated >= $newest_source) }"; then
        info "Generated Unicode extension tables are up to date"
        exit 0
    fi
fi

info "Regenerating Unicode extension tables from $ucd_root"
python3 scripts/build/generate_unicode_tables.py --ucd-root "$ucd_root" --out ext/hartonomous_pg/src/generated
generated_unicode_tables_present || die "Unicode generator completed but expected outputs are missing"
