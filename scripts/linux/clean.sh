#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

managed=false
native=false
all=false

while (($#)); do
    case "$1" in
        --managed) managed=true; shift ;;
        --native) native=true; shift ;;
        --all) all=true; shift ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/clean.sh [--managed] [--native] [--all]
Remove build outputs. Defaults to --all when no scope is supplied.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

if [[ "$managed" == false && "$native" == false && "$all" == false ]]; then
    all=true
fi

if [[ "$all" == true || "$managed" == true ]]; then
    info "Cleaning managed bin/obj directories"
    find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
fi

if [[ "$all" == true || "$native" == true ]]; then
    info "Cleaning native build directories"
    rm -rf ext/libhartonomous/build ext/libhartonomous/out ext/hartonomous_pg/build
fi
