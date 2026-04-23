#!/bin/bash
# Hartonomous postgres entrypoint.
# - First boot: run initdb, run /docker-entrypoint-initdb.d/*.sql
# - Subsequent boots: just exec postgres.
set -euo pipefail

PGDATA="${PGDATA:-/var/lib/postgresql/data}"
PG_BIN=/opt/pg18/bin

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

    # Performance tuning carried over from prior compose config.
    cat >> "$PGDATA/postgresql.conf" <<EOF
shared_buffers = 1GB
work_mem = 64MB
maintenance_work_mem = 256MB
max_wal_size = 8GB
synchronous_commit = off
shared_preload_libraries = 'hartonomous'
EOF

    # Start postgres temporarily to run init scripts.
    "$PG_BIN/pg_ctl" -D "$PGDATA" -o "-c listen_addresses=''" -w start

    if [ -d /docker-entrypoint-initdb.d ]; then
        for f in /docker-entrypoint-initdb.d/*.sql; do
            [ -e "$f" ] || continue
            echo "[hartonomous-entrypoint] Running $f"
            "$PG_BIN/psql" -v ON_ERROR_STOP=1 --username=postgres --dbname=postgres -f "$f"
        done
    fi

    "$PG_BIN/pg_ctl" -D "$PGDATA" -m fast -w stop
    echo "[hartonomous-entrypoint] Initialization complete."
fi

# CMD is "postgres" (or any pg binary). Resolve via PG_BIN.
cmd="$1"; shift || true
exec "$PG_BIN/$cmd" "$@"
