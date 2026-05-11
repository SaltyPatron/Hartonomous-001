#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

args=(down)
while (($#)); do
    case "$1" in
        --volumes)
            args+=(-v)
            shift
            ;;
        --remove-orphans)
            args+=(--remove-orphans)
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/docker-down.sh [--volumes] [--remove-orphans]
Stop the compose stack. --volumes destroys pgdata. No PowerShell.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd docker
docker_compose "${args[@]}"
