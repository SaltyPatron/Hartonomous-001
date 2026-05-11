#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

build=false

while (($#)); do
    case "$1" in
        --build)
            build=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/docker-up.sh [--build]
Run docker compose up -d and wait for hartonomous-postgres health. No PowerShell.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd docker

args=(up -d)
if [[ "$build" == true ]]; then
    args+=(--build)
fi

POSTGRES_PORT="$POSTGRES_PORT" POSTGRES_USER="$POSTGRES_USER" POSTGRES_PASSWORD="$POSTGRES_PASSWORD" POSTGRES_DB="$POSTGRES_DB" \
    docker_compose "${args[@]}"
wait_container_healthy 120
