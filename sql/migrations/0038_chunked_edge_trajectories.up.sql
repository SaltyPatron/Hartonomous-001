-- 0038_chunked_edge_trajectories.up.sql
-- Replace the 0036 batched implementation of substrate.populate_edge_trajectories
-- with a keyset-chunked variant driven by the caller.
--
-- Background: 0036 used a `WHERE geom IS NULL LIMIT N` loop. Since edge.geom
-- has no partial index on NULL, every iteration was a sequential scan of the
-- partition (20M+ rows in edge_default after UD), so the function was O(N²).
-- A single-pass UPDATE on the same data crashed Postgres with signal 11
-- (likely temp-file exhaustion materializing a 24M-row CTE).
--
-- New design: the function takes an explicit (low, high) id range and runs a
-- single bounded UPDATE inside one transaction. The caller (Engine
-- NpgsqlIngestionPipeline.PopulateEdgeTrajectoriesAsync) iterates the id space
-- in chunks, committing between chunks. Per-chunk memory is bounded by the
-- chunk size, the planner uses the edge_pkey btree to bound the scan, and a
-- crash inside one chunk only loses that chunk's work.
--
-- Direction semantics unchanged from 0036: (source -> target) when both roles
-- are present, else (dependent -> head) for UD dependency edges, with endpoints
-- resolved through substrate.entity_s3_point.

DROP FUNCTION IF EXISTS substrate.populate_edge_trajectories(text, integer);

CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(
    p_id_low  bigint,
    p_id_high bigint
)
RETURNS bigint
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated bigint;
BEGIN
    WITH edge_endpoints AS (
        SELECT
            e.id AS edge_id,
            COALESCE(src.entity_id, dep.entity_id) AS start_entity_id,
            COALESCE(tgt.entity_id, hd.entity_id)  AS end_entity_id
        FROM substrate.edge e
        LEFT JOIN substrate.edge_member src
               ON src.edge_id = e.id AND src.edge_role_id = 1   -- source
        LEFT JOIN substrate.edge_member tgt
               ON tgt.edge_id = e.id AND tgt.edge_role_id = 2   -- target
        LEFT JOIN substrate.edge_member dep
               ON dep.edge_id = e.id AND dep.edge_role_id = 7   -- dependent
        LEFT JOIN substrate.edge_member hd
               ON hd.edge_id  = e.id AND hd.edge_role_id  = 6   -- head
        WHERE e.id >= p_id_low
          AND e.id <  p_id_high
          AND e.geom IS NULL
    ),
    with_points AS (
        SELECT
            ep.edge_id,
            substrate.entity_s3_point(ep.start_entity_id) AS start_point,
            substrate.entity_s3_point(ep.end_entity_id)   AS end_point
        FROM edge_endpoints ep
        WHERE ep.start_entity_id IS NOT NULL
          AND ep.end_entity_id   IS NOT NULL
    )
    UPDATE substrate.edge e
    SET geom = ST_MakeLine(wp.start_point, wp.end_point)
    FROM with_points wp
    WHERE e.id = wp.edge_id
      AND wp.start_point IS NOT NULL
      AND wp.end_point   IS NOT NULL;

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RETURN v_updated;
END;
$$;

COMMENT ON FUNCTION substrate.populate_edge_trajectories(bigint, bigint) IS
    'Compute edge.geom = LINESTRINGZM(start, end) for every NULL-geom edge whose '
    'id falls in [p_id_low, p_id_high). Endpoints come from either '
    '(source, target) or (dependent, head) role pairs in that priority order, '
    'resolved through substrate.entity_s3_point. Caller drives id-range '
    'iteration so each invocation is a bounded transaction.';
