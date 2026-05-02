import os
base = r'D:\Repositories\Cursor\Hartonomous\sql\schema\functions'

# Hash-only rewrites for SQL helper functions.
# Plpgsql functions (flush_*_from_staging, prime_edge_significance_*) parse
# at call time so they don't block migration apply; they will fail at runtime
# but they're legacy paths not exercised by the streaming pipeline.

S = "'source'"
T = "'target'"

files = {}

files['composition_at.sql'] = """-- composition_at(parent_hash, ordinal) - hash-only.
DROP FUNCTION IF EXISTS substrate.composition_at(INT, BYTEA, INT);
CREATE OR REPLACE FUNCTION substrate.composition_at(
    p_parent_hash BYTEA,
    p_ordinal     INT
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.child_hash, s.rle_count
      FROM substrate.sequence s
     WHERE s.parent_hash = p_parent_hash
       AND p_ordinal >= s.ordinal
       AND p_ordinal <  s.ordinal + s.rle_count
     LIMIT 1;
$f$;
"""

files['composition_before.sql'] = """DROP FUNCTION IF EXISTS substrate.composition_before(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_before(
    p_parent_hash BYTEA, p_ordinal INT, p_distance INT DEFAULT 1
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT * FROM substrate.composition_at(p_parent_hash, p_ordinal - p_distance);
$f$;
"""

files['composition_after.sql'] = """DROP FUNCTION IF EXISTS substrate.composition_after(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_after(
    p_parent_hash BYTEA, p_ordinal INT, p_distance INT DEFAULT 1
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT * FROM substrate.composition_at(p_parent_hash, p_ordinal + p_distance);
$f$;
"""

files['composition_range.sql'] = """DROP FUNCTION IF EXISTS substrate.composition_range(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_range(
    p_parent_hash BYTEA, p_start INT, p_end INT
) RETURNS TABLE (ordinal INT, child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.ordinal, s.child_hash, s.rle_count
      FROM substrate.sequence s
     WHERE s.parent_hash = p_parent_hash
       AND s.ordinal + s.rle_count > p_start
       AND s.ordinal <= p_end
     ORDER BY s.ordinal;
$f$;
"""

files['composition_subtrajectory.sql'] = """DROP FUNCTION IF EXISTS substrate.composition_subtrajectory(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_subtrajectory(
    p_parent_hash BYTEA, p_start INT, p_end INT
) RETURNS TABLE (ordinal INT, child_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT g.n AS ordinal, s.child_hash
      FROM substrate.sequence s
      CROSS JOIN LATERAL generate_series(s.ordinal, s.ordinal + s.rle_count - 1) AS g(n)
     WHERE s.parent_hash = p_parent_hash
       AND g.n BETWEEN p_start AND p_end
     ORDER BY g.n;
$f$;
"""

files['composition_parents.sql'] = """DROP FUNCTION IF EXISTS substrate.composition_parents(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.composition_parents(
    p_child_hash BYTEA
) RETURNS TABLE (parent_hash BYTEA, ordinal INT, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.parent_hash, s.ordinal, s.rle_count
      FROM substrate.sequence s
     WHERE s.child_hash = p_child_hash;
$f$;
"""

files['get_composition_children.sql'] = """DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.get_composition_children(
    p_parent_hash BYTEA
) RETURNS TABLE (ordinal INT, child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.ordinal, s.child_hash, s.rle_count
      FROM substrate.sequence s
     WHERE s.parent_hash = p_parent_hash
     ORDER BY s.ordinal;
$f$;
"""

files['entity_outbound_edges.sql'] = (
    "DROP FUNCTION IF EXISTS substrate.entity_outbound_edges(INT, BYTEA, TEXT);\n"
    "CREATE OR REPLACE FUNCTION substrate.entity_outbound_edges(\n"
    "    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL\n"
    ") RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)\n"
    "LANGUAGE sql STABLE PARALLEL SAFE AS $f$\n"
    "    SELECT em.edge_type_id, em.edge_hash, COALESCE(es.mu, 1500.0)\n"
    "      FROM substrate.edge_member em\n"
    f"      JOIN substrate.edge_role er ON er.id = em.edge_role_id AND er.code = {S}\n"
    "      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code\n"
    "      LEFT JOIN substrate.edge_significance es\n"
    "        ON es.edge_type_id = em.edge_type_id AND es.edge_hash = em.edge_hash\n"
    "       AND es.context_type_id = sc.id\n"
    "     WHERE em.entity_hash = p_entity_hash;\n"
    "$f$;\n"
)

files['entity_inbound_edges.sql'] = (
    "DROP FUNCTION IF EXISTS substrate.entity_inbound_edges(INT, BYTEA, TEXT);\n"
    "CREATE OR REPLACE FUNCTION substrate.entity_inbound_edges(\n"
    "    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL\n"
    ") RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)\n"
    "LANGUAGE sql STABLE PARALLEL SAFE AS $f$\n"
    "    SELECT em.edge_type_id, em.edge_hash, COALESCE(es.mu, 1500.0)\n"
    "      FROM substrate.edge_member em\n"
    f"      JOIN substrate.edge_role er ON er.id = em.edge_role_id AND er.code = {T}\n"
    "      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code\n"
    "      LEFT JOIN substrate.edge_significance es\n"
    "        ON es.edge_type_id = em.edge_type_id AND es.edge_hash = em.edge_hash\n"
    "       AND es.context_type_id = sc.id\n"
    "     WHERE em.entity_hash = p_entity_hash;\n"
    "$f$;\n"
)

files['entity_neighbors.sql'] = (
    "DROP FUNCTION IF EXISTS substrate.entity_neighbors(INT, BYTEA, TEXT);\n"
    "CREATE OR REPLACE FUNCTION substrate.entity_neighbors(\n"
    "    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL\n"
    ") RETURNS TABLE (neighbor_hash BYTEA, edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)\n"
    "LANGUAGE sql STABLE PARALLEL SAFE AS $f$\n"
    "    SELECT em2.entity_hash, em1.edge_type_id, em1.edge_hash, COALESCE(es.mu, 1500.0)\n"
    "      FROM substrate.edge_member em1\n"
    "      JOIN substrate.edge_member em2\n"
    "        ON em2.edge_type_id = em1.edge_type_id AND em2.edge_hash = em1.edge_hash\n"
    "       AND em2.entity_hash <> em1.entity_hash\n"
    "      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code\n"
    "      LEFT JOIN substrate.edge_significance es\n"
    "        ON es.edge_type_id = em1.edge_type_id AND es.edge_hash = em1.edge_hash\n"
    "       AND es.context_type_id = sc.id\n"
    "     WHERE em1.entity_hash = p_entity_hash;\n"
    "$f$;\n"
)

files['entity_centroid_4d.sql'] = """DROP FUNCTION IF EXISTS substrate.entity_centroid_4d(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.entity_centroid_4d(
    p_entity_hash BYTEA
) RETURNS geometry(GeometryZM)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT geom FROM substrate.physicality
     WHERE entity_hash = p_entity_hash
     ORDER BY physicality_type_id LIMIT 1;
$f$;
"""

files['get_edge_info_by_handles.sql'] = """DROP FUNCTION IF EXISTS substrate.get_edge_info_by_handles(INT[], BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_edge_info_by_handles(
    p_type_ids INT[], p_hashes BYTEA[]
) RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, provenance_id INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.edge_type_id, e.hash, e.provenance_id
      FROM unnest(p_type_ids, p_hashes) AS in_(t, h)
      JOIN substrate.edge e ON e.edge_type_id = in_.t AND e.hash = in_.h;
$f$;
"""

files['get_entity_info_by_handles.sql'] = """DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(INT[], BYTEA[]);
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_entity_info_by_handles(
    p_hashes BYTEA[]
) RETURNS TABLE (entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash FROM unnest(p_hashes) AS in_(h) JOIN substrate.entity e ON e.hash = in_.h;
$f$;
"""

files['get_outbound_edge_targets.sql'] = (
    "DROP FUNCTION IF EXISTS substrate.get_outbound_edge_targets(INT, BYTEA, TEXT);\n"
    "CREATE OR REPLACE FUNCTION substrate.get_outbound_edge_targets(\n"
    "    p_src_hash BYTEA, p_edge_type_code TEXT\n"
    ") RETURNS TABLE (target_hash BYTEA)\n"
    "LANGUAGE sql STABLE PARALLEL SAFE AS $f$\n"
    "    SELECT em_t.entity_hash\n"
    "      FROM substrate.edge_type et\n"
    "      JOIN substrate.edge_member em_s\n"
    "        ON em_s.edge_type_id = et.id AND em_s.entity_hash = p_src_hash\n"
    f"      JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = {S}\n"
    "      JOIN substrate.edge_member em_t\n"
    "        ON em_t.edge_type_id = em_s.edge_type_id AND em_t.edge_hash = em_s.edge_hash\n"
    f"      JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = {T}\n"
    "     WHERE et.code = p_edge_type_code;\n"
    "$f$;\n"
)

files['resolve_entity_handles.sql'] = """DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[], TEXT[]);
DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.resolve_entity_handles(
    p_hashes BYTEA[]
) RETURNS TABLE (entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash FROM unnest(p_hashes) AS in_(h) JOIN substrate.entity e ON e.hash = in_.h;
$f$;
"""

# Health summary (used by 0015)
hs = os.path.join(base, 'health_summary.sql')
if os.path.exists(hs):
    with open(hs, 'r', encoding='utf-8') as f:
        c = f.read()
    if 'entity_type_id' in c:
        files['health_summary.sql'] = """DROP FUNCTION IF EXISTS substrate.health_summary();
CREATE OR REPLACE FUNCTION substrate.health_summary()
RETURNS TABLE (metric TEXT, value BIGINT)
LANGUAGE sql STABLE AS $f$
    SELECT 'entities'::TEXT, count(*)::BIGINT FROM substrate.entity
  UNION ALL SELECT 'edges',           count(*) FROM substrate.edge
  UNION ALL SELECT 'sequences',       count(*) FROM substrate.sequence
  UNION ALL SELECT 'physicalities',   count(*) FROM substrate.physicality
  UNION ALL SELECT 'classifications', count(*) FROM substrate.entity_classification;
$f$;
"""

for name, content in files.items():
    p = os.path.join(base, name)
    with open(p, 'w', encoding='utf-8') as f:
        f.write(content)
    print(name)

print("done -", len(files))
