#!/usr/bin/env bash
set -Eeuo pipefail

repo_root() {
    local dir
    dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
    printf '%s\n' "$dir"
}

ROOT="${HARTONOMOUS_REPO_ROOT:-$(repo_root)}"
cd "$ROOT"

DOTNET_CONFIGURATION="${HARTONOMOUS_DOTNET__CONFIGURATION:-Debug}"
NATIVE_CONFIGURATION="${HARTONOMOUS_NATIVE__CONFIGURATION:-Release}"
NATIVE_BUILD_DIR="${HARTONOMOUS_PATHS__NATIVEBUILD:-ext/libhartonomous/build}"

SCRIPT_NAME="$(basename "${0:-}")"
if [[ -n "${HARTONOMOUS_DB_RUNTIME:-}" ]]; then
    DB_RUNTIME="$HARTONOMOUS_DB_RUNTIME"
elif [[ "$SCRIPT_NAME" == docker-* ]]; then
    DB_RUNTIME="docker"
else
    DB_RUNTIME="local"
fi

PG_CONTAINER="${HARTONOMOUS_DOCKER__PGCONTAINER:-hartonomous-postgres}"
if [[ "$DB_RUNTIME" == "docker" ]]; then
    DEFAULT_POSTGRES_HOST="localhost"
    DEFAULT_POSTGRES_PORT="5433"
    DEFAULT_POSTGRES_USER="hartonomous"
    DEFAULT_POSTGRES_PASSWORD="hartonomous"
else
    DEFAULT_POSTGRES_HOST="/var/run/postgresql"
    DEFAULT_POSTGRES_PORT="5432"
    DEFAULT_POSTGRES_USER="${USER:-postgres}"
    DEFAULT_POSTGRES_PASSWORD=""
fi

POSTGRES_HOST="${HARTONOMOUS_POSTGRES__HOST:-$DEFAULT_POSTGRES_HOST}"
POSTGRES_PORT="${HARTONOMOUS_POSTGRES__PORT:-$DEFAULT_POSTGRES_PORT}"
POSTGRES_USER="${HARTONOMOUS_POSTGRES__USER:-$DEFAULT_POSTGRES_USER}"
POSTGRES_PASSWORD="${HARTONOMOUS_POSTGRES__PASSWORD:-$DEFAULT_POSTGRES_PASSWORD}"
POSTGRES_DB="${HARTONOMOUS_POSTGRES__DATABASE:-hartonomous}"
POSTGRES_MAINTENANCE_DB="${HARTONOMOUS_POSTGRES__MAINTENANCEDATABASE:-postgres}"
if [[ -n "$POSTGRES_PASSWORD" ]]; then
    DEFAULT_HARTONOMOUS_DB="Host=${POSTGRES_HOST};Port=${POSTGRES_PORT};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD};Database=${POSTGRES_DB}"
else
    DEFAULT_HARTONOMOUS_DB="Host=${POSTGRES_HOST};Port=${POSTGRES_PORT};Username=${POSTGRES_USER};Database=${POSTGRES_DB}"
fi
HARTONOMOUS_DB="${HARTONOMOUS_DB:-$DEFAULT_HARTONOMOUS_DB}"
SOURCE_ROOT="${HARTONOMOUS_PATHS__SOURCEROOT:-${HARTONOMOUS_SOURCE_ROOT:-/vault/Data}}"
UCD_ROOT="${HARTONOMOUS_UCD_ROOT:-${SOURCE_ROOT}/Unicode/Public/UCD/latest}"

info() {
    printf '==> %s\n' "$*"
}

warn() {
    printf 'WARN: %s\n' "$*" >&2
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

require_cmd() {
    command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

require_pg_identifier() {
    local value="$1"
    local label="$2"
    [[ "$value" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || die "$label must be a PostgreSQL identifier: $value"
}

docker_compose() {
    if docker compose version >/dev/null 2>&1; then
        docker compose "$@"
    elif command -v docker-compose >/dev/null 2>&1; then
        docker-compose "$@"
    else
        die "docker compose plugin or docker-compose is required"
    fi
}

container_running() {
    [[ -n "$(docker ps -q --filter "name=^${PG_CONTAINER}$")" ]]
}

container_exists() {
    [[ -n "$(docker ps -a -q --filter "name=^${PG_CONTAINER}$")" ]]
}

wait_container_healthy() {
    local timeout="${1:-120}"
    local start now status
    start="$(date +%s)"
    while true; do
        status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$PG_CONTAINER" 2>/dev/null || true)"
        if [[ "$status" == "healthy" || "$status" == "running" ]]; then
            info "$PG_CONTAINER is $status"
            return 0
        fi
        now="$(date +%s)"
        if (( now - start >= timeout )); then
            docker logs --tail 120 "$PG_CONTAINER" >&2 || true
            die "$PG_CONTAINER did not become healthy within ${timeout}s (last status: ${status:-missing})"
        fi
        sleep 2
    done
}

psql_in_container() {
    local database="${1:-$POSTGRES_DB}"
    shift || true
    docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$PG_CONTAINER" \
        psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$database" "$@"
}

pg_bin() {
    local name="$1"
    local configured_var="HARTONOMOUS_${name^^}"
    local configured="${!configured_var:-}"
    if [[ -n "$configured" ]]; then
        printf '%s\n' "$configured"
    else
        command -v "$name" || true
    fi
}

pg_config_bin() {
    if [[ -n "${HARTONOMOUS_PG_CONFIG:-}" ]]; then
        printf '%s\n' "$HARTONOMOUS_PG_CONFIG"
    else
        command -v pg_config || true
    fi
}

psql_local() {
    local database="${1:-$POSTGRES_DB}"
    shift || true
    local psql_bin
    psql_bin="$(pg_bin psql)"
    [[ -n "$psql_bin" ]] || die "required command not found: psql"
    PGPASSWORD="$POSTGRES_PASSWORD" "$psql_bin" -v ON_ERROR_STOP=1 \
        -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d "$database" "$@"
}

psql_db() {
    local database="${1:-$POSTGRES_DB}"
    shift || true
    case "$DB_RUNTIME" in
        local) psql_local "$database" "$@" ;;
        docker)
            require_cmd docker
            container_running || die "$PG_CONTAINER is not running"
            psql_in_container "$database" "$@"
            ;;
        *) die "unknown HARTONOMOUS_DB_RUNTIME: $DB_RUNTIME" ;;
    esac
}

pg_command_env() {
    export PGHOST="$POSTGRES_HOST"
    export PGPORT="$POSTGRES_PORT"
    export PGUSER="$POSTGRES_USER"
    export PGPASSWORD="$POSTGRES_PASSWORD"
}

install_command() {
    if [[ -n "${HARTONOMOUS_INSTALL:-}" ]]; then
        printf '%s\n' "$HARTONOMOUS_INSTALL"
    elif [[ -w "$(dirname "$1")" ]]; then
        printf 'install\n'
    else
        printf '%s\n' "${HARTONOMOUS_SUDO:-sudo install}"
    fi
}

run_dotnet_test_project() {
    local project="$1"
    shift
    info "dotnet test $project"
    dotnet test "$project" -c "$DOTNET_CONFIGURATION" --nologo "$@"
}

run_cli() {
    local no_build=false
    if [[ "${1:-}" == "--no-build" ]]; then
        no_build=true
        shift
    fi

    local args=(run --project src/Hartonomous.Cli/Hartonomous.Cli.csproj -c "$DOTNET_CONFIGURATION")
    if [[ "$no_build" == true ]]; then
        args+=(--no-build)
    fi
    args+=(-- "$@")
    HARTONOMOUS__Hartonomous__ConnectionString="$HARTONOMOUS_DB" dotnet "${args[@]}"
}

generated_unicode_tables_present() {
    local dir="ext/hartonomous_pg/src/generated"
    local expected=(
        pg_unicode_version.h
        pg_ucd_segmentation.h pg_ucd_segmentation.c
        pg_ucd_classification.h pg_ucd_classification.c
        pg_ucd_casing.h pg_ucd_casing.c
        pg_ucd_pictographic.h pg_ucd_pictographic.c
        pg_ucd_decomp.h pg_ucd_decomp.c
        pg_ucd_fcf.h pg_ucd_fcf.c
        pg_ucd_uca.h pg_ucd_uca.c
        pg_ucd_names.h pg_ucd_names.c
        pg_ucd_inventory.h pg_ucd_inventory.c
        pg_ucd_tier1.h pg_ucd_tier1.c
        pg_ucd_atoms_blob.h
        pg_ucd.h
    )

    for file in "${expected[@]}"; do
        [[ -s "$dir/$file" ]] || return 1
    done
}
