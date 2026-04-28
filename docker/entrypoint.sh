#!/bin/bash
# Hartonomous postgres entrypoint.
# - First boot: run initdb, run /docker-entrypoint-initdb.d/*.sql
# - Subsequent boots: just exec postgres.
set -euo pipefail

PGDATA="${PGDATA:-/var/lib/postgresql/data}"
PG_BIN=/opt/pg18/bin

# Standard postgres-image env-var contract: docker-compose.yml passes these in,
# the entrypoint creates the matching role + database on first boot.
APP_USER="${POSTGRES_USER:-hartonomous}"
APP_PASS="${POSTGRES_PASSWORD:-hartonomous}"
APP_DB="${POSTGRES_DB:-hartonomous}"

if [ ! -s "$PGDATA/PG_VERSION" ]; then
    echo "[hartonomous-entrypoint] Initializing new cluster at $PGDATA"
    "$PG_BIN/initdb" \
        --pgdata="$PGDATA" \
        --username=postgres \
        --encoding=UTF8 \
        --locale=en_US.UTF-8 \
        --auth-local=trust \
        --auth-host=scram-sha-256

    # Allow remote connections (Docker network).
    echo "host all all 0.0.0.0/0 scram-sha-256" >> "$PGDATA/pg_hba.conf"
    echo "listen_addresses = '*'" >> "$PGDATA/postgresql.conf"

    # Bootstrap-only settings. Production values are overridden by the
    # `command:` section in docker-compose.yml on every container start, so
    # entrypoint.sh only needs the minimum required for first-boot init
    # (extension loading) plus a sane fallback shape.
    cat >> "$PGDATA/postgresql.conf" <<EOF
shared_buffers = 1GB
work_mem = 64MB
maintenance_work_mem = 256MB
max_wal_size = 8GB
synchronous_commit = off
# hartonomous extension loaded lazily via CREATE EXTENSION + per-call dlopen,
# NOT via shared_preload_libraries. Preloading runs the extension's
# _PG_init() in every backend at fork time and corrupts backend memory
# state even when subsequent queries (e.g. INSERT INTO substrate.entity)
# don't call any extension function. The seed phases use only stock PG +
# PostGIS; extension functions are needed at query/inference time and
# load on demand when those functions are first invoked in a session.
EOF

    # Start postgres temporarily to run role/db creation + init scripts.
    "$PG_BIN/pg_ctl" -D "$PGDATA" -o "-c listen_addresses=''" -w start

    # Create the application role + database. Init scripts run inside the
    # application database as the application user so CREATE EXTENSION lands
    # there, not in the bootstrap `postgres` db.
    echo "[hartonomous-entrypoint] Creating role '$APP_USER' and database '$APP_DB'"
    "$PG_BIN/psql" -v ON_ERROR_STOP=1 --username=postgres --dbname=postgres <<SQL
CREATE ROLE "$APP_USER" WITH LOGIN SUPERUSER PASSWORD '$APP_PASS';
CREATE DATABASE "$APP_DB" OWNER "$APP_USER";
SQL

    if [ -d /docker-entrypoint-initdb.d ]; then
        for f in /docker-entrypoint-initdb.d/*.sql; do
            [ -e "$f" ] || continue
            echo "[hartonomous-entrypoint] Running $f as $APP_USER on $APP_DB"
            "$PG_BIN/psql" -v ON_ERROR_STOP=1 --username="$APP_USER" --dbname="$APP_DB" -f "$f"
        done
    fi

    "$PG_BIN/pg_ctl" -D "$PGDATA" -m fast -w stop
    echo "[hartonomous-entrypoint] Initialization complete."
fi

# CMD is "postgres" (or any pg binary). Resolve via PG_BIN.
cmd="$1"; shift || true
exec "$PG_BIN/$cmd" "$@"
