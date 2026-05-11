#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

force=false
bootstrap=true

while (($#)); do
    case "$1" in
        --force)
            force=true
            shift
            ;;
        --no-bootstrap)
            bootstrap=false
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/db-reset.sh --force [--no-bootstrap]
Drop and recreate the Hartonomous database, then install the extension unless --no-bootstrap is set.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

[[ "$force" == true ]] || die "db-reset is destructive; pass --force"
require_pg_identifier "$POSTGRES_DB" "database name"

info "Terminating connections to $POSTGRES_DB"
psql_db "$POSTGRES_MAINTENANCE_DB" -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '${POSTGRES_DB}' AND pid <> pg_backend_pid()"
info "Dropping database $POSTGRES_DB"
if [[ "$DB_RUNTIME" == "docker" ]]; then
    psql_db "$POSTGRES_MAINTENANCE_DB" -c "DROP DATABASE IF EXISTS \"${POSTGRES_DB}\""
else
    dropdb_bin="$(pg_bin dropdb)"
    [[ -n "$dropdb_bin" ]] || die "required command not found: dropdb"
    pg_command_env
    "$dropdb_bin" --if-exists "$POSTGRES_DB"
fi
scripts/linux/db-create.sh
if [[ "$bootstrap" == true ]]; then
    scripts/linux/db-bootstrap.sh
fi
