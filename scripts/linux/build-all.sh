#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

skip_native=false
skip_dotnet=false
skip_pg_extension=false
clean=false

while (($#)); do
    case "$1" in
        --skip-native) skip_native=true; shift ;;
        --skip-dotnet) skip_dotnet=true; shift ;;
        --skip-pg-extension) skip_pg_extension=true; shift ;;
        --clean) clean=true; shift ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/build-all.sh [--skip-native] [--skip-dotnet] [--skip-pg-extension] [--clean]
Build native, extension SQL, .NET, and the local PG extension. Unicode table generation is explicit codegen, not build/seed.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

if [[ "$skip_native" == false ]]; then
    native_args=()
    [[ "$clean" == true ]] && native_args+=(--clean)
    scripts/linux/build-native.sh "${native_args[@]}"
fi

scripts/linux/build-extension-sql.sh

if [[ "$skip_dotnet" == false ]]; then
    dotnet_args=()
    [[ "$clean" == false ]] && dotnet_args+=(--no-restore)
    scripts/linux/build-dotnet.sh "${dotnet_args[@]}"
fi

if [[ "$skip_pg_extension" == false ]]; then
    scripts/linux/build-pg-extension.sh
fi
