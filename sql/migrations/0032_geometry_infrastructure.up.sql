-- 0032_geometry_infrastructure.up.sql
-- Geometry infrastructure: edge trajectory population, contour similarity,
-- frayed edge detection, analogy completion, and recursive label resolution.
-- This migration turns the substrate from scaffolding into fabric.

-- ═══════════════════════════════════════════════════════════════════
-- 1. Entity representative point — 4D centroid from physicality
-- ═══════════════════════════════════════════════════════════════════
-- Returns the s3_position POINTZM if available, otherwise computes
-- the 4D centroid of the entity's contour LINESTRINGZM.

CREATE OR REPLACE FUNCTION substrate.entity_s3_point(p_entity_id bigint)
RETURNS geometry
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    -- Priority 1: direct s3_position (physicality_type_id = 1)
    -- Priority 2: centroid of contour (physicality_type_id = 13)
    SELECT COALESCE(
        (SELECT geom FROM substrate.physicality
         WHERE entity_id = p_entity_id AND physicality_type_id = 1
         LIMIT 1),
        (SELECT ST_MakePoint(
                    avg(ST_X(pt.geom)),
                    avg(ST_Y(pt.geom)),
                    avg(ST_Z(pt.geom)),
                    avg(ST_M(pt.geom)))
         FROM substrate.physicality p,
              LATERAL ST_DumpPoints(p.geom) AS pt
         WHERE p.entity_id = p_entity_id AND p.physicality_type_id = 13
        )
    );
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 2. Populate edge trajectories — set-based, not RBAR
-- ═══════════════════════════════════════════════════════════════════
-- Computes edge.geom = LINESTRINGZM(source_s3_point → target_s3_point)
-- for all edges whose source and target entities have physicality.
-- Optional filter by edge_type_code. Returns count of edges updated.

CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(
    p_edge_type_code text DEFAULT NULL,
    p_batch_size     integer DEFAULT 50000
)
RETURNS bigint
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_total   bigint := 0;
    v_updated bigint;
    v_edge_type_id integer;
BEGIN
    -- Resolve edge type filter
    IF p_edge_type_code IS NOT NULL THEN
        SELECT id INTO v_edge_type_id
        FROM substrate.edge_type WHERE code = p_edge_type_code;
        IF v_edge_type_id IS NULL THEN
            RAISE EXCEPTION 'Unknown edge type: %', p_edge_type_code;
        END IF;
    END IF;

    -- Process in batches to avoid locking the entire table
    LOOP
        WITH batch AS (
            SELECT e.id AS edge_id
            FROM substrate.edge e
            WHERE e.geom IS NULL
              AND (v_edge_type_id IS NULL OR e.edge_type_id = v_edge_type_id)
            LIMIT p_batch_size
        ),
        edge_endpoints AS (
            SELECT
                b.edge_id,
                src.entity_id AS source_id,
                tgt.entity_id AS target_id
            FROM batch b
            JOIN substrate.edge_member src ON src.edge_id = b.edge_id AND src.edge_role_id = 1  -- source
            JOIN substrate.edge_member tgt ON tgt.edge_id = b.edge_id AND tgt.edge_role_id = 2  -- target
        ),
        with_points AS (
            SELECT
                ep.edge_id,
                -- Source point: direct s3_position or contour centroid
                COALESCE(
                    sp.geom,
                    (SELECT ST_MakePoint(
                                avg(ST_X(dpt.geom)), avg(ST_Y(dpt.geom)),
                                avg(ST_Z(dpt.geom)), avg(ST_M(dpt.geom)))
                     FROM substrate.physicality sc_p,
                          LATERAL ST_DumpPoints(sc_p.geom) AS dpt
                     WHERE sc_p.entity_id = ep.source_id AND sc_p.physicality_type_id = 13)
                ) AS src_point,
                -- Target point: direct s3_position or contour centroid
                COALESCE(
                    tp.geom,
                    (SELECT ST_MakePoint(
                                avg(ST_X(dpt.geom)), avg(ST_Y(dpt.geom)),
                                avg(ST_Z(dpt.geom)), avg(ST_M(dpt.geom)))
                     FROM substrate.physicality tc_p,
                          LATERAL ST_DumpPoints(tc_p.geom) AS dpt
                     WHERE tc_p.entity_id = ep.target_id AND tc_p.physicality_type_id = 13)
                ) AS tgt_point
            FROM edge_endpoints ep
            LEFT JOIN substrate.physicality sp
                ON sp.entity_id = ep.source_id AND sp.physicality_type_id = 1  -- s3_position
            LEFT JOIN substrate.physicality tp
                ON tp.entity_id = ep.target_id AND tp.physicality_type_id = 1  -- s3_position
        )
        UPDATE substrate.edge e
        SET geom = ST_MakeLine(wp.src_point, wp.tgt_point)
        FROM with_points wp
        WHERE e.id = wp.edge_id
          AND wp.src_point IS NOT NULL
          AND wp.tgt_point IS NOT NULL;

        GET DIAGNOSTICS v_updated = ROW_COUNT;
        v_total := v_total + v_updated;

        -- Exit when no more rows to process
        EXIT WHEN v_updated = 0;

        RAISE NOTICE 'populate_edge_trajectories: updated % edges (% total)', v_updated, v_total;
    END LOOP;

    RETURN v_total;
END;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 3. Recursive entity label — handles gc→cp 2-level traversal
-- ═══════════════════════════════════════════════════════════════════
-- Replaces the broken entity_labels which only handles direct codepoint children.
-- This uses recompose_text's recursive CTE approach for correctness.

CREATE OR REPLACE FUNCTION substrate.entity_label(p_entity_id bigint)
RETURNS text
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT substrate.recompose_text(p_entity_id);
$$;

-- Batch version: returns (entity_id, label) for an array of entity IDs.
-- Uses LATERAL to call recompose_text per entity — still set-based at the SQL level.
CREATE OR REPLACE FUNCTION substrate.entity_labels_recursive(p_entity_ids bigint[])
RETURNS TABLE(entity_id bigint, label text)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT u.id, substrate.recompose_text(u.id)
    FROM unnest(p_entity_ids) AS u(id);
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 4. Find entity by pre-computed BLAKE3 hash
-- ═══════════════════════════════════════════════════════════════════
-- O(1) lookup. BLAKE3 hashing is a C# compute facade responsibility
-- (BaseDecomposer.ComputeHash). SQL receives pre-computed hashes only.
CREATE OR REPLACE FUNCTION substrate.find_by_hash(
    p_hash             bytea,
    p_entity_type_code text DEFAULT NULL
)
RETURNS TABLE(entity_id bigint, entity_type_code varchar)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT e.id, et.code
    FROM substrate.entity e
    JOIN substrate.entity_type et ON et.id = e.entity_type_id
    WHERE e.hash = p_hash
      AND (p_entity_type_code IS NULL OR et.code = p_entity_type_code);
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 5. Similar contours — ST_FrechetDistance on physicality contours
-- ═══════════════════════════════════════════════════════════════════
-- Given an entity, find other entities whose contour (LINESTRINGZM)
-- is within a Fréchet distance threshold. Uses GiST pre-filter
-- via ST_DWithin on bounding boxes, then exact Fréchet refinement.

CREATE OR REPLACE FUNCTION substrate.similar_contours(
    p_entity_id  bigint,
    p_threshold  float8 DEFAULT 1.0,
    p_limit      integer DEFAULT 20
)
RETURNS TABLE(entity_id bigint, frechet_distance float8, entity_type_code varchar)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH ref AS (
        SELECT geom
        FROM substrate.physicality
        WHERE entity_id = p_entity_id AND physicality_type_id = 13  -- contour
        LIMIT 1
    )
    SELECT p.entity_id,
           ST_FrechetDistance(ref.geom, p.geom) AS frechet_distance,
           et.code
    FROM ref,
         substrate.physicality p
    JOIN substrate.entity e ON e.id = p.entity_id
    JOIN substrate.entity_type et ON et.id = e.entity_type_id
    WHERE p.physicality_type_id = 13
      AND p.entity_id <> p_entity_id
      AND ST_DWithin(ref.geom, p.geom, p_threshold)  -- GiST bounding box pre-filter
      AND ST_FrechetDistance(ref.geom, p.geom) <= p_threshold
    ORDER BY ST_FrechetDistance(ref.geom, p.geom)
    LIMIT p_limit;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 6. Similar edges — ST_FrechetDistance on edge trajectories
-- ═══════════════════════════════════════════════════════════════════
-- Given an edge, find other edges whose trajectory (geom LINESTRINGZM)
-- is geometrically similar. Enables relation clustering and pattern discovery.

CREATE OR REPLACE FUNCTION substrate.similar_edges(
    p_edge_id    bigint,
    p_threshold  float8 DEFAULT 1.0,
    p_limit      integer DEFAULT 20
)
RETURNS TABLE(edge_id bigint, frechet_distance float8, edge_type_code varchar)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH ref AS (
        SELECT geom FROM substrate.edge WHERE id = p_edge_id
    )
    SELECT e.id,
           ST_FrechetDistance(ref.geom, e.geom) AS frechet_distance,
           et.code
    FROM ref,
         substrate.edge e
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    WHERE e.id <> p_edge_id
      AND e.geom IS NOT NULL
      AND ST_DWithin(ref.geom, e.geom, p_threshold)
      AND ST_FrechetDistance(ref.geom, e.geom) <= p_threshold
    ORDER BY ST_FrechetDistance(ref.geom, e.geom)
    LIMIT p_limit;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 7. Frayed edge detection
-- ═══════════════════════════════════════════════════════════════════
-- At the boundary of documented knowledge, the fabric frays.
-- Entity pairs whose S3 positions predict a relation (within Fréchet
-- threshold of the known distribution for that edge type) but
-- no edge has ever been recorded between them.
--
-- Algorithm:
-- 1. Sample existing edges of the given type to establish the
--    "typical trajectory" distribution.
-- 2. Find entity pairs whose representative points would produce
--    a trajectory within threshold of that distribution.
-- 3. Filter out pairs that already have an edge of that type.

CREATE OR REPLACE FUNCTION substrate.frayed_edges(
    p_edge_type_code  text,
    p_threshold       float8 DEFAULT 1.0,
    p_sample_size     integer DEFAULT 1000,
    p_limit           integer DEFAULT 100
)
RETURNS TABLE(
    source_entity_id  bigint,
    target_entity_id  bigint,
    predicted_distance float8,
    source_label      text,
    target_label      text
)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_edge_type_id integer;
    v_ref_geom     geometry;
BEGIN
    -- Resolve edge type
    SELECT id INTO v_edge_type_id
    FROM substrate.edge_type WHERE code = p_edge_type_code;
    IF v_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'Unknown edge type: %', p_edge_type_code;
    END IF;

    -- Compute the centroid trajectory of existing edges of this type
    -- (average source point → average target point as the "archetype" trajectory)
    SELECT ST_MakeLine(
               ST_MakePoint(avg(ST_X(ST_StartPoint(e.geom))),
                            avg(ST_Y(ST_StartPoint(e.geom))),
                            avg(ST_Z(ST_StartPoint(e.geom))),
                            avg(ST_M(ST_StartPoint(e.geom)))),
               ST_MakePoint(avg(ST_X(ST_EndPoint(e.geom))),
                            avg(ST_Y(ST_EndPoint(e.geom))),
                            avg(ST_Z(ST_EndPoint(e.geom))),
                            avg(ST_M(ST_EndPoint(e.geom))))
           )
    INTO v_ref_geom
    FROM (
        SELECT geom FROM substrate.edge
        WHERE edge_type_id = v_edge_type_id AND geom IS NOT NULL
        ORDER BY random()
        LIMIT p_sample_size
    ) e;

    IF v_ref_geom IS NULL THEN
        RAISE NOTICE 'No populated edge trajectories for type %', p_edge_type_code;
        RETURN;
    END IF;

    -- Find entity pairs that fit this trajectory pattern but lack the edge
    RETURN QUERY
    WITH candidate_sources AS (
        -- Entities that participate as sources in edges of this type
        -- Use their entity type as a constraint for finding new sources
        SELECT DISTINCT e2.entity_type_id
        FROM substrate.edge e
        JOIN substrate.edge_member em ON em.edge_id = e.id AND em.edge_role_id = 1
        JOIN substrate.entity e2 ON e2.id = em.entity_id
        WHERE e.edge_type_id = v_edge_type_id
        LIMIT 5
    ),
    candidate_targets AS (
        SELECT DISTINCT e2.entity_type_id
        FROM substrate.edge e
        JOIN substrate.edge_member em ON em.edge_id = e.id AND em.edge_role_id = 2
        JOIN substrate.entity e2 ON e2.id = em.entity_id
        WHERE e.edge_type_id = v_edge_type_id
        LIMIT 5
    ),
    src_entities AS (
        SELECT p.entity_id, p.geom AS src_geom
        FROM substrate.physicality p
        JOIN substrate.entity ent ON ent.id = p.entity_id
        WHERE p.physicality_type_id IN (1, 13)
          AND ent.entity_type_id IN (SELECT entity_type_id FROM candidate_sources)
          -- Bounding box pre-filter: source point should be near archetype start
          AND ST_DWithin(
              CASE WHEN p.physicality_type_id = 1 THEN p.geom
                   ELSE (SELECT ST_MakePoint(
                             avg(ST_X(dpt.geom)), avg(ST_Y(dpt.geom)),
                             avg(ST_Z(dpt.geom)), avg(ST_M(dpt.geom)))
                         FROM ST_DumpPoints(p.geom) AS dpt)
              END,
              ST_StartPoint(v_ref_geom),
              p_threshold * 2)
        LIMIT p_limit * 10
    ),
    tgt_entities AS (
        SELECT p.entity_id, p.geom AS tgt_geom
        FROM substrate.physicality p
        JOIN substrate.entity ent ON ent.id = p.entity_id
        WHERE p.physicality_type_id IN (1, 13)
          AND ent.entity_type_id IN (SELECT entity_type_id FROM candidate_targets)
          AND ST_DWithin(
              CASE WHEN p.physicality_type_id = 1 THEN p.geom
                   ELSE (SELECT ST_MakePoint(
                             avg(ST_X(dpt.geom)), avg(ST_Y(dpt.geom)),
                             avg(ST_Z(dpt.geom)), avg(ST_M(dpt.geom)))
                         FROM ST_DumpPoints(p.geom) AS dpt)
              END,
              ST_EndPoint(v_ref_geom),
              p_threshold * 2)
        LIMIT p_limit * 10
    ),
    candidate_pairs AS (
        SELECT
            s.entity_id AS src_id,
            t.entity_id AS tgt_id,
            ST_FrechetDistance(
                v_ref_geom,
                ST_MakeLine(
                    substrate.entity_s3_point(s.entity_id),
                    substrate.entity_s3_point(t.entity_id)
                )
            ) AS dist
        FROM src_entities s
        CROSS JOIN tgt_entities t
        WHERE s.entity_id <> t.entity_id
    )
    SELECT
        cp.src_id,
        cp.tgt_id,
        cp.dist,
        substrate.recompose_text(cp.src_id),
        substrate.recompose_text(cp.tgt_id)
    FROM candidate_pairs cp
    WHERE cp.dist <= p_threshold
      -- Exclude pairs that already have this edge type
      AND NOT EXISTS (
          SELECT 1
          FROM substrate.edge_member em_s
          JOIN substrate.edge_member em_t ON em_t.edge_id = em_s.edge_id
          JOIN substrate.edge ex ON ex.id = em_s.edge_id
          WHERE em_s.entity_id = cp.src_id AND em_s.edge_role_id = 1
            AND em_t.entity_id = cp.tgt_id AND em_t.edge_role_id = 2
            AND ex.edge_type_id = v_edge_type_id
      )
    ORDER BY cp.dist
    LIMIT p_limit;
END;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 8. Edge analogy completion — king:queen :: man:?
-- ═══════════════════════════════════════════════════════════════════
-- Given entities A, B, C: find D such that the trajectory from C→D
-- is similar to the trajectory from A→B.
-- Uses ST_FrechetDistance on 2-point LINESTRINGZM trajectories.

CREATE OR REPLACE FUNCTION substrate.edge_analogy(
    p_a_id       bigint,   -- e.g. "king"
    p_b_id       bigint,   -- e.g. "queen"
    p_c_id       bigint,   -- e.g. "man"
    p_threshold  float8 DEFAULT 2.0,
    p_limit      integer DEFAULT 10
)
RETURNS TABLE(entity_id bigint, frechet_distance float8, entity_type_code varchar, label text)
LANGUAGE sql STABLE
AS $$
    WITH
    -- Compute the reference trajectory: A → B
    ab_trajectory AS (
        SELECT ST_MakeLine(
                   substrate.entity_s3_point(p_a_id),
                   substrate.entity_s3_point(p_b_id)
               ) AS geom
    ),
    -- Get C's representative point
    c_point AS (
        SELECT substrate.entity_s3_point(p_c_id) AS geom
    ),
    -- Compute offset vector: B - A (in 4D)
    -- Apply to C to get predicted D position
    predicted_d AS (
        SELECT ST_MakePoint(
            ST_X(c_point.geom) + (ST_X(substrate.entity_s3_point(p_b_id)) - ST_X(substrate.entity_s3_point(p_a_id))),
            ST_Y(c_point.geom) + (ST_Y(substrate.entity_s3_point(p_b_id)) - ST_Y(substrate.entity_s3_point(p_a_id))),
            ST_Z(c_point.geom) + (ST_Z(substrate.entity_s3_point(p_b_id)) - ST_Z(substrate.entity_s3_point(p_a_id))),
            ST_M(c_point.geom) + (ST_M(substrate.entity_s3_point(p_b_id)) - ST_M(substrate.entity_s3_point(p_a_id)))
        ) AS geom
        FROM c_point
    )
    -- Find entities near the predicted D position
    SELECT
        p.entity_id,
        ST_FrechetDistance(
            (SELECT geom FROM ab_trajectory),
            ST_MakeLine(c_point.geom, substrate.entity_s3_point(p.entity_id))
        ) AS frechet_distance,
        et.code,
        substrate.recompose_text(p.entity_id)
    FROM predicted_d,
         c_point,
         substrate.physicality p
    JOIN substrate.entity e ON e.id = p.entity_id
    JOIN substrate.entity_type et ON et.id = e.entity_type_id
    WHERE p.physicality_type_id IN (1, 13)
      AND p.entity_id <> p_c_id
      AND p.entity_id <> p_a_id
      AND p.entity_id <> p_b_id
      -- Bounding box pre-filter: near predicted D position
      AND ST_DWithin(
          CASE WHEN p.physicality_type_id = 1 THEN p.geom
               ELSE (SELECT ST_MakePoint(
                         avg(ST_X(dpt.geom)), avg(ST_Y(dpt.geom)),
                         avg(ST_Z(dpt.geom)), avg(ST_M(dpt.geom)))
                     FROM ST_DumpPoints(p.geom) AS dpt)
          END,
          predicted_d.geom,
          p_threshold)
    ORDER BY frechet_distance
    LIMIT p_limit;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 9. Convergence views — cross-decomposer overlap statistics
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE VIEW substrate.convergence_summary AS
WITH entity_provenance AS (
    SELECT DISTINCT
        em.entity_id,
        prov.code AS provenance_code
    FROM substrate.edge_member em
    JOIN substrate.edge e ON e.id = em.edge_id
    JOIN substrate.provenance prov ON prov.id = e.provenance_id
),
multi_provenance AS (
    SELECT entity_id, count(DISTINCT provenance_code) AS prov_count,
           array_agg(DISTINCT provenance_code ORDER BY provenance_code) AS provenances
    FROM entity_provenance
    GROUP BY entity_id
    HAVING count(DISTINCT provenance_code) > 1
)
SELECT
    provenances,
    count(*) AS shared_entities,
    prov_count AS provenance_count
FROM multi_provenance
GROUP BY provenances, prov_count
ORDER BY shared_entities DESC;

-- ═══════════════════════════════════════════════════════════════════
-- 10. Geometry coverage view — how much of the substrate has geometry
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE VIEW substrate.geometry_coverage AS
SELECT
    et.code AS entity_type,
    count(e.id) AS total_entities,
    count(DISTINCT p_s3.entity_id) AS with_s3_position,
    count(DISTINCT p_ct.entity_id) AS with_contour,
    round(100.0 * count(DISTINCT COALESCE(p_s3.entity_id, p_ct.entity_id)) / GREATEST(count(e.id), 1), 1) AS coverage_pct
FROM substrate.entity e
JOIN substrate.entity_type et ON et.id = e.entity_type_id
LEFT JOIN substrate.physicality p_s3
    ON p_s3.entity_id = e.id AND p_s3.physicality_type_id = 1
LEFT JOIN substrate.physicality p_ct
    ON p_ct.entity_id = e.id AND p_ct.physicality_type_id = 13
GROUP BY et.code
ORDER BY total_entities DESC;

CREATE OR REPLACE VIEW substrate.edge_trajectory_coverage AS
SELECT
    et.code AS edge_type,
    count(e.id) AS total_edges,
    count(e.geom) AS with_trajectory,
    round(100.0 * count(e.geom) / GREATEST(count(e.id), 1), 1) AS coverage_pct
FROM substrate.edge e
JOIN substrate.edge_type et ON et.id = e.edge_type_id
WHERE EXISTS (SELECT 1 FROM substrate.edge e2 WHERE e2.edge_type_id = et.id)
GROUP BY et.code
HAVING count(e.id) > 0
ORDER BY total_edges DESC;
