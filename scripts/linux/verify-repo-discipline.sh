#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

strict=false
max_findings=120
write_inventory=""

while (($#)); do
    case "$1" in
        --strict)
            strict=true
            shift
            ;;
        --max-findings)
            max_findings="${2:?missing max findings}"
            shift 2
            ;;
        --write-inventory)
            write_inventory="${2:?missing inventory path}"
            shift 2
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/verify-repo-discipline.sh [--strict] [--max-findings N] [--write-inventory PATH]

Run Linux-native repository discipline inventory and guardrail checks. Default
mode reports findings and exits 0; --strict exits non-zero when findings exist.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd python3

args=(--max-findings "$max_findings")
if [[ "$strict" == true ]]; then
    args+=(--strict)
fi
if [[ -n "$write_inventory" ]]; then
    args+=(--write-inventory "$write_inventory")
fi

python3 scripts/verify/repo_discipline.py "${args[@]}"
