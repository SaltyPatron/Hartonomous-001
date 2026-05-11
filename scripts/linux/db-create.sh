#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

case "$DB_RUNTIME" in
    local|docker) ;;
    *) die "unknown HARTONOMOUS_DB_RUNTIME: $DB_RUNTIME" ;;
esac
require_pg_identifier "$POSTGRES_DB" "database name"
require_pg_identifier "$POSTGRES_USER" "database owner"

exists="$(psql_db "$POSTGRES_MAINTENANCE_DB" -Atc "SELECT 1 FROM pg_database WHERE datname = '${POSTGRES_DB}'" || true)"
if [[ "$exists" == "1" ]]; then
    info "Database $POSTGRES_DB already exists"
else
    info "Creating database $POSTGRES_DB"
    if [[ "$DB_RUNTIME" == "docker" ]]; then
        psql_db "$POSTGRES_MAINTENANCE_DB" -c "CREATE DATABASE \"${POSTGRES_DB}\" OWNER \"${POSTGRES_USER}\""
    else
        createdb_bin="$(pg_bin createdb)"
        [[ -n "$createdb_bin" ]] || die "required command not found: createdb"
        pg_command_env
        "$createdb_bin" -O "$POSTGRES_USER" "$POSTGRES_DB"
    fi
fi

postgis_available="$(psql_db "$POSTGRES_MAINTENANCE_DB" -Atc "SELECT count(*) FROM pg_available_extensions WHERE name = 'postgis'")"
[[ "$postgis_available" != "0" ]] || die "postgis extension is not available to PostgreSQL at ${POSTGRES_HOST}:${POSTGRES_PORT}"
