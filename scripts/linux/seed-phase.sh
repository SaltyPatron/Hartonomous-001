#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

phase="${1:-}"
[[ -n "$phase" ]] || die "usage: scripts/linux/seed-phase.sh PHASE [--source PATH] [--skip-deps] [--force] [--no-build]"
shift

source_root="$SOURCE_ROOT"
no_build=false
extra=()

while (($#)); do
    case "$1" in
        --source) source_root="${2:?missing source root}"; shift 2 ;;
        --skip-deps|--force) extra+=("$1"); shift ;;
        --no-build) no_build=true; shift ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/seed-phase.sh PHASE [--source PATH] [--skip-deps] [--force] [--no-build]
Run one seed phase through the canonical C# phase runner.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

args=(run --phase "$phase" --source "$source_root" "${extra[@]}")
[[ "$no_build" == true ]] && args+=(--no-build)
scripts/linux/phases.sh "${args[@]}"
