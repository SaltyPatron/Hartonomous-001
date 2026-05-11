#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

skip_db=false
no_build=false

while (($#)); do
    case "$1" in
        --skip-db)
            skip_db=true
            shift
            ;;
        --no-build)
            no_build=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/test-all.sh [--skip-db] [--no-build]
Run native + DB-free .NET unit tests, then DB-backed pg/smoke/integration tests against HARTONOMOUS_DB.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

dotnet_args=()
if [[ "$no_build" == true ]]; then
    dotnet_args+=(--no-build)
fi

scripts/linux/test-native.sh
scripts/linux/test-dotnet-unit.sh "${dotnet_args[@]}"

if [[ "$skip_db" == true ]]; then
    info "Skipping DB-backed tests"
    exit 0
fi

psql_db "$POSTGRES_DB" -Atc "SELECT 1" >/dev/null
scripts/linux/test-pg.sh
scripts/linux/test-smoke.sh "${dotnet_args[@]}"
scripts/linux/test-integration.sh "${dotnet_args[@]}"
