#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

install_check=false
clean=false
install_mode=copy

while (($#)); do
    case "$1" in
        --install-check)
            install_check=true
            shift
            ;;
        --clean)
            clean=true
            shift
            ;;
        --copy)
            install_mode=copy
            shift
            ;;
        --symlink)
            install_mode=symlink
            shift
            ;;
        --install-mode|--mode)
            install_mode="${2:?missing install mode}"
            shift 2
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/build-pg-extension.sh [--install-check] [--clean] [--copy|--symlink|--install-mode copy|symlink]
Build and install the PostgreSQL extension into the local PostgreSQL returned by pg_config. Consumes committed generated Unicode extension assets; does not regenerate them.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

scripts/linux/build-extension-sql.sh
generated_unicode_tables_present || die "generated Unicode extension assets missing; run explicit codegen with scripts/hart codegen unicode --ucd-root PATH"

pg_config="$(pg_config_bin)"
[[ -n "$pg_config" ]] || die "required command not found: pg_config"
require_cmd make

native_lib="$NATIVE_BUILD_DIR/bin/libhartonomous.so"
[[ -s "$native_lib" ]] || scripts/linux/build-native.sh --no-tests
[[ -s "$native_lib" ]] || die "native library not found after build: $native_lib"
scripts/linux/install-pg-extension.sh --mode "$install_mode" --native-only

if [[ "$clean" == true ]]; then
    make -C ext/hartonomous_pg PG_CONFIG="$pg_config" clean
fi

info "Building hartonomous PostgreSQL extension with $pg_config"
make -C ext/hartonomous_pg PG_CONFIG="$pg_config"
scripts/linux/install-pg-extension.sh --mode "$install_mode"

if [[ "$install_check" == true ]]; then
    scripts/linux/test-pg.sh
fi
