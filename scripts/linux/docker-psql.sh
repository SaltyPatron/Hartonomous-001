#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

database="$POSTGRES_DB"
sql=""

while (($#)); do
    case "$1" in
        --database|-d) database="${2:?missing database}"; shift 2 ;;
        --sql|-c) sql="${2:?missing SQL}"; shift 2 ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/docker-psql.sh [--database DB] [--sql SQL]
Open psql in hartonomous-postgres or run one SQL command.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

require_cmd docker
container_running || die "$PG_CONTAINER is not running"
if [[ -n "$sql" ]]; then
    psql_in_container "$database" -c "$sql"
else
    docker exec -it -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG_CONTAINER" psql -U "$POSTGRES_USER" -d "$database"
fi
