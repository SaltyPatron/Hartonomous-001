#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

# ──────────────────────────────────────────────────────────────────────
# Three-step install (cross-gate item 4 — schema install separation):
#   1. Create substrate + monitor schemas (so the C-binding script can
#      create functions inside substrate.*).
#   2. CREATE EXTENSION hartonomous CASCADE — installs the .so's C-binding
#      surface (4D types, operators, BLAKE3, traversal, UCD catalog
#      accessors, substrate.cp_*, substrate.text_decompose, etc.) and
#      cascades postgis / btree_gist / pg_trgm.
#   3. Apply ext/hartonomous_pg/sql/substrate-schema.sql via plain psql -f
#      (user mode, no sudo). This installs the substrate schema content —
#      tables, indexes, junctions, seed inserts, substrate.* SQL/plpgsql
#      functions, procedures, views — owned by the user, not the extension.
#
# Splitting the install eliminates the extension-ownership column-strip
# quirk on substrate.entity (GENERATED columns disappearing on
# CREATE EXTENSION) and means only `make install` of the .so requires
# sudo; the schema install is no-sudo.
# ──────────────────────────────────────────────────────────────────────

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

# Step 1: pre-create substrate + monitor schemas. The C-binding script
# (hartonomous--1.0.sql.in) declares substrate.similarity_topk /
# recompose_walk / text_decompose / cp_* / ucd_* etc. and needs the
# substrate schema to exist before they can be created. The script
# itself does NOT contain CREATE SCHEMA statements.
info "Ensuring substrate + monitor schemas exist"
psql_db "$POSTGRES_DB" -c "CREATE SCHEMA IF NOT EXISTS substrate; CREATE SCHEMA IF NOT EXISTS monitor"

# Step 2: install the C-binding surface via CREATE EXTENSION.
info "Installing hartonomous extension (C-binding surface)"
psql_db "$POSTGRES_DB" -c "CREATE EXTENSION IF NOT EXISTS hartonomous CASCADE"

schema_count="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM pg_namespace WHERE nspname IN ('substrate','monitor')")"
[[ "$schema_count" -ge 2 ]] || die "expected substrate and monitor schemas after CREATE EXTENSION; got $schema_count"

# Step 3: apply substrate schema content via plain psql -f (user mode).
substrate_schema_sql="$ROOT/ext/hartonomous_pg/sql/substrate-schema.sql"
if [[ ! -f "$substrate_schema_sql" ]]; then
    info "Generating substrate-schema.sql from sql/schema/*"
    python3 "$ROOT/scripts/build/concat_extension_sql.py" \
        >/dev/null \
        || die "concat_extension_sql.py failed"
    [[ -f "$substrate_schema_sql" ]] \
        || die "substrate-schema.sql still missing after generation: $substrate_schema_sql"
fi

info "Applying substrate-schema.sql (substrate schema, owned by user)"
psql_db "$POSTGRES_DB" -1 -f "$substrate_schema_sql" >/dev/null

# Verification: schemas present, extension installed, entity has full column
# set including centroid_* + hilbert_index.
schema_count="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM pg_namespace WHERE nspname IN ('substrate','monitor')")"
[[ "$schema_count" -ge 2 ]] || die "expected substrate and monitor schemas after substrate-schema apply; got $schema_count"

ext_version="$(psql_db "$POSTGRES_DB" -Atc "SELECT extversion FROM pg_extension WHERE extname = 'hartonomous'" || true)"
[[ -n "$ext_version" ]] || die "hartonomous extension is not installed after CREATE EXTENSION"

entity_col_count="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'substrate' AND table_name = 'entity' AND column_name IN ('hash','hash_bits_0_51','hash_bits_52_103','centroid_x','centroid_y','centroid_z','centroid_m','hilbert_index')")"
[[ "$entity_col_count" -eq 8 ]] || die "substrate.entity is missing columns (got $entity_col_count of 8 expected: hash, hash_bits_0_51, hash_bits_52_103, centroid_x, centroid_y, centroid_z, centroid_m, hilbert_index)"

entity_type_count="$(psql_db "$POSTGRES_DB" -Atc "SELECT count(*) FROM substrate.entity_type")"
[[ "$entity_type_count" -ge 23 ]] || die "substrate.entity_type seed is short (got $entity_type_count, expected >= 23)"

info "Substrate ready: extension $ext_version, substrate.entity has all 8 columns, substrate.entity_type has $entity_type_count rows"
