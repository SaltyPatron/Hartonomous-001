#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

action="${1:-list}"
if (($#)); then shift; fi
phase=""
source_root="$SOURCE_ROOT"
model_source="$MODEL_SOURCE"
no_build=false
extra=()

while (($#)); do
    case "$1" in
        --phase) phase="${2:?missing phase}"; shift 2 ;;
        --source) source_root="${2:?missing source root}"; shift 2 ;;
        --model-source) model_source="${2:?missing model source}"; shift 2 ;;
        --skip-deps|--force|--dry-run) extra+=("$1"); shift ;;
        --no-build) no_build=true; shift ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/phases.sh list|status|run [--phase PHASE] [--source PATH] [--model-source PATH] [--skip-deps] [--force] [--dry-run] [--no-build]
Wrap Hartonomous.Cli phases without PowerShell.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

cli_prefix=()
[[ "$no_build" == true ]] && cli_prefix+=(--no-build)

case "$action" in
    list)
        run_cli "${cli_prefix[@]}" phases list
        ;;
    status)
        run_cli "${cli_prefix[@]}" phases status --connection "$HARTONOMOUS_DB"
        ;;
    run)
        [[ -n "$phase" ]] || die "--phase is required for run"
        run_args=(phases run --phase "$phase" --connection "$HARTONOMOUS_DB" --source "$source_root")
        [[ -n "$model_source" ]] && run_args+=(--model-source "$model_source")
        run_cli "${cli_prefix[@]}" "${run_args[@]}" "${extra[@]}"
        ;;
    *)
        die "unknown action: $action"
        ;;
esac
