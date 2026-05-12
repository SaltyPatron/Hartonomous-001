#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

require_docker=false
skip_cmake=false
while (($#)); do
    case "$1" in
        --require-docker) require_docker=true; shift ;;
        --skip-docker) shift ;;
        --skip-cmake) skip_cmake=true; shift ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/ci-preflight.sh [--require-docker] [--skip-cmake]
Check Linux prerequisites without PowerShell.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

require_cmd dotnet
require_cmd python3
require_cmd pg_config
require_cmd psql
[[ "$skip_cmake" == true ]] || require_cmd cmake
[[ "$require_docker" == true ]] && require_cmd docker
[[ -f Hartonomous.slnx ]] || die "missing Hartonomous.slnx"
[[ -f sql/schema/bootstrap.sql ]] || die "missing sql/schema/bootstrap.sql"
[[ -d ext/libhartonomous ]] || die "missing ext/libhartonomous"
[[ -d ext/hartonomous_pg ]] || die "missing ext/hartonomous_pg"
dotnet --version | awk -F. '{ if ($1 < 9) exit 1 }' || die ".NET SDK 9+ required"
scripts/linux/verify-repo-discipline.sh --strict --max-findings 40
info "Linux preflight checks passed"
