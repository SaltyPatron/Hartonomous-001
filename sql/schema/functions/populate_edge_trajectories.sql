-- Populate edge trajectories from participant centroids.
--
-- Performance + correctness rewrite (was: per-row UDF dispatch + ordered-set
-- aggregate over the full join, which crashed PostGIS aggregate state when
-- the tuplestore spilled to temp files at >800k edges).
--
-- Three changes vs prior:
--   1. LIMIT is pushed onto the edge-selection CTE first. Only the chosen
--      edges' members are joined against physicality, instead of joining
--      ALL members on every call and discarding all but `p_limit` at the
--      end. Cuts the per-call work from O(total_edges × avg_members) to
--      O(p_limit × avg_members).
--   2. `substrate.entity_centroid_4d(entity_hash)` UDF call is replaced
--      with a LATERAL JOIN onto substrate.physicality. plpgsql + PG cannot
--      amortize STABLE-function calls across rows; an explicit JOIN can.
--   3. `ST_MakeLine(... ORDER BY ...)` ordered-set aggregate is replaced by
--      a pre-sorted subquery feeding a plain `ST_MakeLine(arr)` over the
--      array form. PostGIS's ordered-set aggregate path spills to temp
--      files under memory pressure and was the SIGSEGV site (NULL deref at
--      offset 0x17 in tuplestore recovery). The array form materializes in
--      a single pass without spill state.
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
               substrate.geom_to_pointzm(p.geom) AS cgeom
          FROM null_edges ne
          JOIN substrate.edge_member em
            ON em.edge_type_id = ne.edge_type_id
           AND em.edge_hash    = ne.edge_hash
          LEFT JOIN LATERAL (
              SELECT geom
                FROM substrate.physicality ph
               WHERE ph.entity_hash = em.entity_hash
               ORDER BY ph.physicality_type_id
               LIMIT 1
          ) p ON true
    ),
    candidates AS (
        SELECT edge_type_id, edge_hash
          FROM per_edge_pts
         GROUP BY edge_type_id, edge_hash
        HAVING count(*) >= 2
           AND count(cgeom) = count(*)
    ),
    sorted_pts AS (
        SELECT p.edge_type_id, p.edge_hash, p.cgeom,
               row_number() OVER (
                   PARTITION BY p.edge_type_id, p.edge_hash
                   ORDER BY p.edge_role_id, p.role_position, p.entity_hash
               ) AS rn
          FROM per_edge_pts p
          JOIN candidates c
            ON c.edge_type_id = p.edge_type_id
           AND c.edge_hash    = p.edge_hash
    ),
    aggregated AS (
        SELECT edge_type_id,
               edge_hash,
               ST_MakeLine(array_agg(cgeom ORDER BY rn)) AS line_geom,
               count(*) AS member_count
          FROM sorted_pts
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
    'Populate substrate.edge.geom with LINESTRINGZM through participant centroids in role order. LATERAL JOIN onto substrate.physicality (no per-row UDF), pre-sorted array_agg feeding ST_MakeLine (no ordered-set aggregate spill). Edges with missing participant centroids are left NULL.';
