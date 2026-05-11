#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

configuration="$NATIVE_CONFIGURATION"
clean=false
build_tests=ON

while (($#)); do
    case "$1" in
        -c|--configuration)
            configuration="${2:?missing configuration}"
            shift 2
            ;;
        --clean)
            clean=true
            shift
            ;;
        --no-tests)
            build_tests=OFF
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/build-native.sh [--configuration Release|Debug|RelWithDebInfo|MinSizeRel] [--clean] [--no-tests]
Configure and build ext/libhartonomous with CMake on Linux. No PowerShell.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd cmake

if [[ "$clean" == true ]]; then
    info "Removing $NATIVE_BUILD_DIR"
    rm -rf "$NATIVE_BUILD_DIR"
fi

info "Configuring libhartonomous ($configuration)"
cmake -S ext/libhartonomous -B "$NATIVE_BUILD_DIR" \
    -DCMAKE_BUILD_TYPE="$configuration" \
    -DHARTONOMOUS_BUILD_TESTS="$build_tests" \
    -DHARTONOMOUS_BUILD_SHARED=ON

info "Building libhartonomous ($configuration)"
cmake --build "$NATIVE_BUILD_DIR" --config "$configuration" --parallel
