#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

psql_db "$POSTGRES_DB" <<'SQL'
SELECT 'substrate.entity' AS table_name, count(*) AS rows FROM substrate.entity
UNION ALL SELECT 'substrate.edge', count(*) FROM substrate.edge
UNION ALL SELECT 'substrate.edge_member', count(*) FROM substrate.edge_member
UNION ALL SELECT 'substrate.physicality', count(*) FROM substrate.physicality
UNION ALL SELECT 'substrate.entity_classification', count(*) FROM substrate.entity_classification
UNION ALL SELECT 'substrate.edge_significance(primed)', count(*) FROM substrate.edge_significance WHERE games > 0
UNION ALL SELECT 'substrate.entity_significance(primed)', count(*) FROM substrate.entity_significance WHERE games > 0
ORDER BY table_name;

-- Per-arena coverage: how many edges have evidence in each arena.
SELECT sc.code AS arena, count(*) AS edge_rows, count(*) FILTER (WHERE es.games > 0) AS primed_rows
  FROM substrate.edge_significance es
  JOIN substrate.significance_context sc ON sc.id = es.context_type_id
 GROUP BY sc.code
 ORDER BY sc.code;
SQL
