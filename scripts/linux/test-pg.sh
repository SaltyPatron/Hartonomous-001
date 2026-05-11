#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

pg_config="$(pg_config_bin)"
[[ -n "$pg_config" ]] || die "required command not found: pg_config"
require_cmd make

regress_db="contrib_regression"
exists="$(psql_db "$POSTGRES_MAINTENANCE_DB" -Atc "SELECT 1 FROM pg_database WHERE datname = '${regress_db}'" || true)"
if [[ "$exists" == "1" ]]; then
    info "Recreating pg_regress database $regress_db"
    psql_db "$POSTGRES_MAINTENANCE_DB" -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '${regress_db}' AND pid <> pg_backend_pid()"
    if [[ "$DB_RUNTIME" == "docker" ]]; then
        psql_db "$POSTGRES_MAINTENANCE_DB" -c "DROP DATABASE \"${regress_db}\""
    else
        dropdb_bin="$(pg_bin dropdb)"
        [[ -n "$dropdb_bin" ]] || die "required command not found: dropdb"
        pg_command_env
        "$dropdb_bin" "$regress_db"
    fi
fi

info "Creating pg_regress database $regress_db"
if [[ "$DB_RUNTIME" == "docker" ]]; then
    psql_db "$POSTGRES_MAINTENANCE_DB" -c "CREATE DATABASE \"${regress_db}\" OWNER \"${POSTGRES_USER}\""
else
    createdb_bin="$(pg_bin createdb)"
    [[ -n "$createdb_bin" ]] || die "required command not found: createdb"
    pg_command_env
    "$createdb_bin" -O "$POSTGRES_USER" "$regress_db"
fi

pg_command_env
info "Running pg_regress installcheck against ${POSTGRES_HOST}:${POSTGRES_PORT}"
make -C ext/hartonomous_pg PG_CONFIG="$pg_config" installcheck
