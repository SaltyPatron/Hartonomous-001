#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

configuration="$DOTNET_CONFIGURATION"
test_args=()

while (($#)); do
    case "$1" in
        -c|--configuration)
            configuration="${2:?missing configuration}"
            DOTNET_CONFIGURATION="$configuration"
            shift 2
            ;;
        --no-build)
            test_args+=(--no-build)
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/test-integration.sh [--configuration Debug|Release] [--no-build]
Run DB-backed integration tests against HARTONOMOUS_DB. Requires a bootstrapped PostgreSQL database.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd dotnet
psql_db "$POSTGRES_DB" -Atc "SELECT 1" >/dev/null
export HARTONOMOUS_DB
run_dotnet_test_project tests/Hartonomous.Integration.Tests/Hartonomous.Integration.Tests.csproj "${test_args[@]}" --logger "console;verbosity=minimal"
