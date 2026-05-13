#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

source_root="$SOURCE_ROOT"
model_source="$MODEL_SOURCE"
with_model=false
no_build=false

while (($#)); do
    case "$1" in
        --source) source_root="${2:?missing source root}"; shift 2 ;;
        --model-source) model_source="${2:?missing model source}"; shift 2 ;;
        --with-model) with_model=true; shift ;;
        --no-build) no_build=true; shift ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/seed-all.sh [--source PATH] [--model-source PATH] [--with-model] [--no-build]
Run UcdUca, Iso639, WordNetOmw, UniversalDeps, Wiktionary, Tatoeba, optional ModelDecomp, then row-count validation.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

common=(--source "$source_root")
[[ -n "$model_source" ]] && common+=(--model-source "$model_source")
[[ "$no_build" == true ]] && common+=(--no-build)

for phase in UcdUca Iso639 WordNetOmw UniversalDeps Wiktionary Tatoeba; do
    scripts/linux/seed-phase.sh "$phase" "${common[@]}"
done

if [[ "$with_model" == true ]]; then
    scripts/linux/seed-phase.sh ModelDecomp "${common[@]}"
fi

scripts/linux/seed-validate.sh
