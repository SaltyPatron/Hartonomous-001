#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

phase="${1:-}"
[[ -n "$phase" ]] || die "usage: scripts/linux/seed-phase.sh PHASE [--source PATH] [--skip-deps] [--force] [--no-build]"
shift

source_root="$SOURCE_ROOT"
no_build=false
extra=()
force=false

while (($#)); do
    case "$1" in
        --source) source_root="${2:?missing source root}"; shift 2 ;;
        --skip-deps) extra+=("$1"); shift ;;
        --force) force=true; extra+=("$1"); shift ;;
        --no-build) no_build=true; shift ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/seed-phase.sh PHASE [--source PATH] [--skip-deps] [--force] [--no-build]
Run one seed phase through the canonical C# phase runner.

WARNING: --force only resets monitor.phase_status for the phase. It does NOT
remove previously emitted substrate rows, junctions, significance events, or
edges. For a destructive clean-state reseed, drop and rebootstrap the database
with scripts/linux/db-reset.sh --force, then rerun the seed phase.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

if [[ "$force" == true ]]; then
    warn "--force only resets phase checkpoints; it does not clean substrate rows/events"
fi

args=(run --phase "$phase" --source "$source_root" "${extra[@]}")
[[ "$no_build" == true ]] && args+=(--no-build)
scripts/linux/phases.sh "${args[@]}"
