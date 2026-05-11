#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

tail_lines=200
follow=false
while (($#)); do
    case "$1" in
        --tail) tail_lines="${2:?missing tail count}"; shift 2 ;;
        --follow|-f) follow=true; shift ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/docker-logs.sh [--tail N] [--follow]
Show hartonomous-postgres logs.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

require_cmd docker
args=(logs --tail "$tail_lines")
[[ "$follow" == true ]] && args+=(-f)
args+=("$PG_CONTAINER")
docker "${args[@]}"
