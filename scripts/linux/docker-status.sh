#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

require_cmd docker
printf 'Docker daemon: '
if docker info >/dev/null 2>&1; then
    printf 'up\n'
else
    printf 'down\n'
fi
printf 'Container:     %s\n' "$PG_CONTAINER"
printf '  exists:      %s\n' "$(container_exists && echo true || echo false)"
printf '  running:     %s\n' "$(container_running && echo true || echo false)"
if container_exists; then
    printf '  health:      %s\n' "$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$PG_CONTAINER")"
fi
if container_running; then
    printf '  ports:\n'
    docker port "$PG_CONTAINER" | sed 's/^/    /' || true
fi
