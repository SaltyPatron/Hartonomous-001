#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

# ──────────────────────────────────────────────────────────────────────
# PostGIS-pattern install: a single CREATE EXTENSION installs everything.
#
# `hartonomous--1.0.sql` (built by scripts/build/concat_extension_sql.py
# and deployed to $(pg_sharedir)/extension/ via `make install`) contains:
#   (a) CREATE SCHEMA substrate, monitor
#   (b) C-binding declarations from hartonomous--1.0.sql.in (point4d/box4d
#       types + operators, BLAKE3, traversal, substrate.cp_*,
#       substrate.text_decompose, glicko2_bulk_update)
#   (c) Full bootstrap.sql @include walk — domains, types, tables,
#       indexes, seeds, substrate.* SQL/plpgsql functions, procedures,
#       views.
#
# So this script's job is just: ensure the unified .sql is up to date on
# disk, ensure prerequisites (postgis), then CREATE EXTENSION hartonomous
# CASCADE. One transaction, one DROP EXTENSION removes everything cleanly.
# ──────────────────────────────────────────────────────────────────────

# Step 0: keep the deployed extension SQL in sync with the source tree.
# `make install` is what actually copies the file under
# $(pg_sharedir)/extension/, but we regenerate the source-tree copy so a
# subsequent `make install` deploys the freshest content.
ext_sql="$ROOT/ext/hartonomous_pg/sql/hartonomous--1.0.sql"
info "Regenerating $ext_sql from sql/schema/*"
python3 "$ROOT/scripts/build/concat_extension_sql.py" >/dev/null \
    || die "concat_extension_sql.py failed"
[[ -f "$ext_sql" ]] || die "unified extension SQL still missing after generation: $ext_sql"

# Step 1: prerequisite — PostGIS. CREATE EXTENSION hartonomous CASCADE
# also installs it, but we install explicitly to pin the latest available
# numeric version (CASCADE picks the default, which may lag).
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

# Step 2: install the substrate. CREATE EXTENSION runs in an implicit
# transaction; the unified script installs schemas, C-bindings, tables,
# indexes, seeds, and substrate.* SQL functions atomically.
info "Installing hartonomous extension (unified — C bindings + substrate schema)"
psql_db "$POSTGRES_DB" -c "CREATE EXTENSION IF NOT EXISTS hartonomous CASCADE"

# Verification: schemas present, extension installed, substrate.entity is
# identity-only (hash PK; no centroid/hilbert/hash_bits/partition_bucket
# columns — geometry lives on substrate.physicality; vertex reverse-resolve
# uses the functional btree entity_hash_prefix_idx on bb_hash_lo/hi(hash)).
schema_count="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM pg_namespace WHERE nspname IN ('substrate','monitor')")"
[[ "$schema_count" -ge 2 ]] || die "expected substrate and monitor schemas after CREATE EXTENSION; got $schema_count"

ext_version="$(psql_db "$POSTGRES_DB" -Atc "SELECT extversion FROM pg_extension WHERE extname = 'hartonomous'" || true)"
[[ -n "$ext_version" ]] || die "hartonomous extension is not installed after CREATE EXTENSION"

entity_col_count="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'substrate' AND table_name = 'entity' AND column_name = 'hash'")"
[[ "$entity_col_count" -eq 1 ]] || die "substrate.entity must carry the hash column (got $entity_col_count). Identity-only — geometry on substrate.physicality."

entity_type_count="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM substrate.entity_type")"
[[ "$entity_type_count" -ge 23 ]] || die "substrate.entity_type seed is short (got $entity_type_count, expected >= 23)"

info "Substrate ready: extension $ext_version, substrate.entity is identity-only (hash PK), substrate.entity_type has $entity_type_count rows"
