#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

configuration="$DOTNET_CONFIGURATION"
test_args=()
seed_validation=false

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
        --seed-validation|--full-ucd)
            seed_validation=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/test-smoke.sh [--configuration Debug|Release] [--no-build] [--seed-validation]
Run DB-backed smoke tests against HARTONOMOUS_DB. Requires a bootstrapped PostgreSQL database. Seeded-state validation and slow seed-mutation probes run only with --seed-validation.
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
if [[ "$seed_validation" == false ]]; then
    test_args+=(--filter "Category!=SeedValidation&Category!=SeedMutation")
fi
run_dotnet_test_project tests/Hartonomous.Smoke.Tests/Hartonomous.Smoke.Tests.csproj "${test_args[@]}" --logger "console;verbosity=minimal"
