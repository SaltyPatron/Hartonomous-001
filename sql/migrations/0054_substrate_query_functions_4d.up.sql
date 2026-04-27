-- 0054_substrate_query_functions_4d.up.sql
--
-- Replaces the substrate's spatial query functions (similar_contours,
-- similar_edges, edge_analogy from migration 0030) with 4D-aware versions
-- that use substrate.st_4d_frechet_distance instead of PostGIS-native
-- ST_FrechetDistance. Per .claude/rules/25-physicality-4d.md and AP-4:
-- every substrate point is 4D; PostGIS native ST_FrechetDistance projects
-- to 2D and silently drops the M axis. The substrate-side 4D operators
-- (defined in migration 0049) are the only correct distance/centroid/
-- Frechet/Hausdorff calls on substrate physicality.
--
-- Behavioral change: similar_* and edge_analogy now produce results that
-- respect the M axis. Existing callers see different ranking on physicality
-- whose M axis carries semantic information (codepoint S^3 positions where
-- M is the unit-quaternion w-component, embedding fireflies where M is the
-- L2 row-norm salience, every other 4D physicality type).

CREATE OR REPLACE FUNCTION substrate.similar_contours(
    p_entity_id BIGINT,
    p_threshold FLOAT8 DEFAULT 1.0,
    p_limit     INT DEFAULT 20
)
RETURNS TABLE(entity_id BIGINT, frechet_distance FLOAT8, entity_type_code VARCHAR)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH ref AS (
        SELECT geom AS contour
          FROM substrate.physicality
         WHERE entity_id = p_entity_id AND physicality_type_id = 13
         LIMIT 1
    )
    SELECT p.entity_id,
           substrate.st_4d_frechet_distance(ref.contour, p.geom) AS frechet_distance,
           et.code
      FROM ref,
           substrate.physicality p
      JOIN substrate.entity ent ON ent.id = p.entity_id
      JOIN substrate.entity_type et ON et.id = ent.entity_type_id
     WHERE p.physicality_type_id = 13
       AND p.entity_id <> p_entity_id
       AND substrate.st_4d_frechet_distance(ref.contour, p.geom) <= p_threshold
     ORDER BY frechet_distance
     LIMIT p_limit;
$$;

COMMENT ON FUNCTION substrate.similar_contours(BIGINT, FLOAT8, INT) IS
    'Find contours within Frechet distance of a reference contour using 4D-aware substrate.st_4d_frechet_distance. Replaces migration 0030 version that silently dropped M via PostGIS ST_FrechetDistance.';

CREATE OR REPLACE FUNCTION substrate.similar_edges(
    p_edge_id   BIGINT,
    p_threshold FLOAT8 DEFAULT 1.0,
    p_limit     INT DEFAULT 20
)
RETURNS TABLE(edge_id BIGINT, frechet_distance FLOAT8, edge_type_code VARCHAR)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH ref AS (
        SELECT geom FROM substrate.edge WHERE id = p_edge_id
    )
    SELECT e.id,
           substrate.st_4d_frechet_distance(ref.geom, e.geom) AS frechet_distance,
           et.code
    FROM ref,
         substrate.edge e
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    WHERE e.id <> p_edge_id
      AND e.geom IS NOT NULL
      AND ref.geom && ST_Expand(e.geom, p_threshold)
      AND substrate.st_4d_frechet_distance(ref.geom, e.geom) <= p_threshold
    ORDER BY substrate.st_4d_frechet_distance(ref.geom, e.geom)
    LIMIT p_limit;
$$;

COMMENT ON FUNCTION substrate.similar_edges(BIGINT, FLOAT8, INT) IS
    'Find edges with similar 4D trajectory shape to a reference edge. Uses substrate.st_4d_frechet_distance (4D-aware) instead of PostGIS native ST_FrechetDistance (2D-projecting). The && envelope predicate is retained as a cheap GiST prune (4D bounding box overlap via gist_geometry_ops_nd) before the precise 4D Frechet test.';

CREATE OR REPLACE FUNCTION substrate.edge_analogy(
    p_a_id      BIGINT,
    p_b_id      BIGINT,
    p_c_id      BIGINT,
    p_threshold FLOAT8 DEFAULT 2.0,
    p_limit     INT DEFAULT 10
)
RETURNS TABLE(entity_id BIGINT, frechet_distance FLOAT8, entity_type_code VARCHAR, label TEXT)
LANGUAGE sql STABLE
AS $$
    WITH
    a_pt AS (SELECT substrate.entity_s3_point(p_a_id) AS geom),
    b_pt AS (SELECT substrate.entity_s3_point(p_b_id) AS geom),
    c_pt AS (SELECT substrate.entity_s3_point(p_c_id) AS geom),
    ab_trajectory AS (
        SELECT ST_MakeLine((SELECT geom FROM a_pt), (SELECT geom FROM b_pt)) AS geom
    ),
    -- Predicted D = C + (B - A) in all four axes. ST_MakePoint(x,y,z,m) is
    -- PostGIS's 4-arg POINTZM constructor (PostGIS infers ZM from arg count).
    -- Downstream substrate.st_4d_frechet_distance treats all 4 axes as spatial
    -- (unlike PostGIS ST_FrechetDistance which projects to 2D and drops M).
    predicted_d AS (
        SELECT ST_MakePoint(
            ST_X((SELECT geom FROM c_pt)) + (ST_X((SELECT geom FROM b_pt)) - ST_X((SELECT geom FROM a_pt))),
            ST_Y((SELECT geom FROM c_pt)) + (ST_Y((SELECT geom FROM b_pt)) - ST_Y((SELECT geom FROM a_pt))),
            ST_Z((SELECT geom FROM c_pt)) + (ST_Z((SELECT geom FROM b_pt)) - ST_Z((SELECT geom FROM a_pt))),
            ST_M((SELECT geom FROM c_pt)) + (ST_M((SELECT geom FROM b_pt)) - ST_M((SELECT geom FROM a_pt)))
        ) AS geom
    )
    SELECT
        p.entity_id,
        substrate.st_4d_frechet_distance(
            (SELECT geom FROM ab_trajectory),
            ST_MakeLine((SELECT geom FROM c_pt), substrate.entity_s3_point(p.entity_id))
        ) AS frechet_distance,
        et.code,
        substrate.recompose_text(p.entity_id)
      FROM predicted_d,
           substrate.physicality p
      JOIN substrate.entity ent ON ent.id = p.entity_id
      JOIN substrate.entity_type et ON et.id = ent.entity_type_id
     WHERE p.physicality_type_id = 1
       AND p.entity_id NOT IN (p_a_id, p_b_id, p_c_id)
       AND p.geom && ST_Expand(predicted_d.geom, p_threshold)
     ORDER BY frechet_distance
     LIMIT p_limit;
$$;

COMMENT ON FUNCTION substrate.edge_analogy(BIGINT, BIGINT, BIGINT, FLOAT8, INT) IS
    'Analogy completion: A:B :: C:?. Computes predicted D in 4D (C + (B - A)) and finds the closest entity by 4D-aware Frechet distance between the AB trajectory and the C-candidate trajectory. Uses substrate.st_4d_frechet_distance — all four axes participate. Previous version (migration 0030) used ST_FrechetDistance which projected to 2D and gave wrong analogies whenever the M axis carried meaning.';
