#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

postgis_installed="$(psql_db "$POSTGRES_DB" -Atc "SELECT extversion FROM pg_extension WHERE extname = 'postgis'" || true)"
if [[ -z "$postgis_installed" ]]; then
    postgis_version="$(psql_db "$POSTGRES_DB" -Atc "SELECT version FROM pg_available_extension_versions WHERE name = 'postgis' AND version ~ '^[0-9]+(\\.[0-9]+)*$' ORDER BY string_to_array(version, '.')::int[] DESC LIMIT 1")"
    [[ -n "$postgis_version" ]] || die "no numeric PostGIS extension version is available"
    [[ "$postgis_version" =~ ^[0-9]+(\.[0-9]+)*$ ]] || die "unexpected PostGIS version string: $postgis_version"
    info "Installing PostGIS $postgis_version"
    psql_db "$POSTGRES_DB" -c "CREATE EXTENSION IF NOT EXISTS postgis WITH VERSION '${postgis_version}'"
else
    info "PostGIS already installed ($postgis_installed)"
fi

info "Installing hartonomous extension"
psql_db "$POSTGRES_DB" -c "CREATE EXTENSION IF NOT EXISTS hartonomous CASCADE"

schema_count="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM pg_namespace WHERE nspname IN ('substrate','monitor')")"
[[ "$schema_count" -ge 2 ]] || die "expected substrate and monitor schemas after CREATE EXTENSION; got $schema_count"
info "Substrate schemas present"
