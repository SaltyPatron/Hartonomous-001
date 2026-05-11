#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

psql_db "$POSTGRES_DB" <<'SQL'
SELECT 'substrate.entity' AS table_name, count(*) AS rows FROM substrate.entity
UNION ALL SELECT 'substrate.edge', count(*) FROM substrate.edge
UNION ALL SELECT 'substrate.edge_member', count(*) FROM substrate.edge_member
UNION ALL SELECT 'substrate.physicality', count(*) FROM substrate.physicality
UNION ALL SELECT 'substrate.entity_classification', count(*) FROM substrate.entity_classification
UNION ALL SELECT 'substrate.sequence', count(*) FROM substrate.sequence
ORDER BY table_name;
SQL
