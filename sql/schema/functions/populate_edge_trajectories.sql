-- Populate edge trajectories from participant identity-POINTZMs in role
-- order. Each participant entity's identity-POINTZM is derived from its
-- BLAKE3 hash mantissa-packed into (X, Z) via substrate.bb_pack_hash_lo /
-- bb_pack_hash_hi — the same encoding composition LINESTRINGZM vertices
-- use, so edge.geom and composition.geom share one structural-identity
-- coordinate system. R-tree GiST indexes (gist_geometry_ops_nd) prune
-- across edge.geom and physicality.geom uniformly.
--
-- The Y mantissa carries the role-position (1-based ordinal in role-sorted
-- member order) via substrate.bb_pack_ordinal_rle with rle_count=1. M is
-- 0 (reserved for future per-edge metadata).
--
-- Performance: edge selection CTE pushes LIMIT FIRST so only the chosen
-- edges' members are joined. ST_MakeLine over an ordered array materializes
-- in one pass without the ordered-set-aggregate tuplestore-spill pattern
-- that previously SIGSEGV'd at >800k edges.
CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(p_limit INT DEFAULT NULL)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated BIGINT;
    v_effective_limit INT := COALESCE(p_limit, 2147483647);
BEGIN
    WITH null_edges AS (
        SELECT edge_type_id, hash AS edge_hash
          FROM substrate.edge
         WHERE geom IS NULL
         ORDER BY edge_type_id, hash
         LIMIT v_effective_limit
    ),
    per_edge_pts AS MATERIALIZED (
        SELECT em.edge_type_id,
               em.edge_hash,
               em.edge_role_id,
               em.role_position,
               em.entity_hash,
               e.hash_bits_0_51,
               e.hash_bits_52_103
          FROM null_edges ne
          JOIN substrate.edge_member em
            ON em.edge_type_id = ne.edge_type_id
           AND em.edge_hash    = ne.edge_hash
          LEFT JOIN substrate.entity e
            ON e.hash = em.entity_hash
    ),
    candidates AS (
        SELECT edge_type_id, edge_hash
          FROM per_edge_pts
         GROUP BY edge_type_id, edge_hash
        HAVING count(*) >= 2
           AND count(hash_bits_0_51) = count(*)
    ),
    sorted_pts AS (
        SELECT p.edge_type_id,
               p.edge_hash,
               p.hash_bits_0_51,
               p.hash_bits_52_103,
               row_number() OVER (
                   PARTITION BY p.edge_type_id, p.edge_hash
                   ORDER BY p.edge_role_id, p.role_position, p.entity_hash
               ) AS rn
          FROM per_edge_pts p
          JOIN candidates c
            ON c.edge_type_id = p.edge_type_id
           AND c.edge_hash    = p.edge_hash
    ),
    vertex_pts AS (
        SELECT edge_type_id,
               edge_hash,
               rn,
               ST_MakePoint(
                   substrate.bb_pack_hash_lo(hash_bits_0_51),
                   substrate.bb_pack_ordinal_rle(rn::INT, 1),
                   substrate.bb_pack_hash_hi(hash_bits_52_103),
                   substrate.bb_pack_metadata(0)
               ) AS pt
          FROM sorted_pts
    ),
    aggregated AS (
        SELECT edge_type_id,
               edge_hash,
               ST_MakeLine(array_agg(pt ORDER BY rn)) AS line_geom,
               count(*) AS member_count
          FROM vertex_pts
         GROUP BY edge_type_id, edge_hash
    )
    UPDATE substrate.edge e
       SET geom = a.line_geom
      FROM aggregated a
     WHERE e.edge_type_id = a.edge_type_id
       AND e.hash         = a.edge_hash
       AND e.geom IS NULL
       AND a.member_count >= 2
       AND a.line_geom IS NOT NULL
       AND ST_NumPoints(a.line_geom) >= 2;

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RETURN v_updated;
END $$;

COMMENT ON FUNCTION substrate.populate_edge_trajectories(INT) IS
    'Populate substrate.edge.geom with LINESTRINGZM through participants'' mantissa-packed identity-POINTZMs in role order. Vertex = (bb_pack_hash_lo(hash_bits_0_51), bb_pack_ordinal_rle(role_position, 1), bb_pack_hash_hi(hash_bits_52_103), bb_pack_metadata(0)). Same encoding composition LINESTRINGZMs use, so edge.geom and composition.geom share one structural-identity coordinate system. Edges with missing participant entities are left NULL and retried on subsequent calls.';
