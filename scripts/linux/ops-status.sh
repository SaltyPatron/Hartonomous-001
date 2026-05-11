#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

printf 'DB runtime:           %s\n' "$DB_RUNTIME"
printf 'PostgreSQL target:    %s:%s/%s as %s\n' "$POSTGRES_HOST" "$POSTGRES_PORT" "$POSTGRES_DB" "$POSTGRES_USER"
require_pg_identifier "$POSTGRES_DB" "database name"

db_exists="$(psql_db "$POSTGRES_MAINTENANCE_DB" -Atc "SELECT 1 FROM pg_database WHERE datname = '${POSTGRES_DB}'" || true)"
printf 'Database exists:      %s\n' "$([[ "$db_exists" == "1" ]] && echo true || echo false)"
if [[ "$db_exists" != "1" ]]; then
    exit 0
fi

postgis="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM pg_extension WHERE extname = 'postgis'" || true)"
hartonomous="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM pg_extension WHERE extname = 'hartonomous'" || true)"
printf 'PostGIS:              %s\n' "$([[ "$postgis" != "0" ]] && echo true || echo false)"
printf 'hartonomous ext:      %s\n' "$([[ "$hartonomous" != "0" ]] && echo true || echo false)"

if [[ "$hartonomous" != "0" ]]; then
    printf '\nPhase status:\n'
    psql_db "$POSTGRES_DB" -c "SELECT phase_code, status, completed_at FROM monitor.phase_status ORDER BY phase_code" || true
    printf '\nSubstrate counts:\n'
    scripts/linux/seed-validate.sh || true
fi
