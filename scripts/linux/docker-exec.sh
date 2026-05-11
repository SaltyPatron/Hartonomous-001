#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

require_cmd docker
container_running || die "$PG_CONTAINER is not running"
if (($# == 0)); then
    set -- bash
fi
docker exec -it "$PG_CONTAINER" "$@"
