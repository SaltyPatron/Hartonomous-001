#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

configuration="$NATIVE_CONFIGURATION"
rebuild=false

while (($#)); do
    case "$1" in
        -c|--configuration)
            configuration="${2:?missing configuration}"
            shift 2
            ;;
        --rebuild)
            rebuild=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/test-native.sh [--configuration Release|Debug|RelWithDebInfo|MinSizeRel] [--rebuild]
Run native ctest suite from ext/libhartonomous/build. No PowerShell.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd ctest

if [[ "$rebuild" == true || ! -d "$NATIVE_BUILD_DIR" ]]; then
    scripts/linux/build-native.sh --configuration "$configuration"
fi

info "Running native tests"
ctest --test-dir "$NATIVE_BUILD_DIR" -C "$configuration" --output-on-failure
