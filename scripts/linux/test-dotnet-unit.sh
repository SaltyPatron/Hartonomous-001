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
        --no-restore)
            test_args+=(--no-restore)
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/test-dotnet-unit.sh [--configuration Debug|Release] [--no-build] [--no-restore]
Run DB-free .NET unit test projects only. Excludes Smoke and Integration by construction.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd dotnet

mapfile -t projects < <(find tests -mindepth 2 -maxdepth 2 -name '*.csproj' \
    ! -path '*/Hartonomous.Smoke.Tests/*' \
    ! -path '*/Hartonomous.Integration.Tests/*' \
    | sort)

for project in "${projects[@]}"; do
    [[ -f "$project" ]] || die "missing test project: $project"
    project_dir="$(dirname "$project")"
    if ! grep -RqsE '\[(Fact|Theory)\b' "$project_dir" --include='*.cs'; then
        info "Skipping $project; no xUnit facts/theories"
        continue
    fi
    run_dotnet_test_project "$project" "${test_args[@]}" --logger "console;verbosity=minimal"
done
