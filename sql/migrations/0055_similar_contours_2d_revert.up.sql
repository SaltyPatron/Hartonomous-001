-- 0055_similar_contours_2d_revert.up.sql
--
-- Migration 0054 incorrectly switched substrate.similar_contours to use
-- substrate.st_4d_frechet_distance. The function queries physicality_type=13
-- (contour), which per docs/specs/sql/mantissa-exploitation.md is a Mode-B
-- physicality: image contours stored as LINESTRINGZM with the convention
--   X = pixel x, Y = pixel y, Z = edge strength, M = contour-id bitmask.
-- Z and M are covering payload, NOT metric coordinates. 4D Frechet would
-- mix edge-strength values and bitmask integers into the distance and
-- produce meaningless rankings.
--
-- The correct operator for image-contour shape similarity is
-- ST_FrechetDistance (PostGIS native, 2D projection on X/Y) — comparing
-- the (pixel_x, pixel_y) curve shape and ignoring Z/M.
--
-- This migration reverts similar_contours to its 2D form. similar_edges
-- and edge_analogy from migration 0054 stay 4D because they operate on
-- substrate.edge.geom and physicality_type=1 (s3_position) respectively,
-- both of which are Mode-A 4D-metric coordinates.
--
-- The general rule: PostGIS GeometryZM is used in the substrate in two
-- modes — Mode A (4D-metric: substrate.st_4d_*) and Mode B (mantissa-
-- exploitation: PostGIS-native on primary axes + ST_Z/ST_M for payload
-- filtering). Operator choice must match the physicality_type's mode.

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
           ST_FrechetDistance(ref.contour, p.geom) AS frechet_distance,
           et.code
      FROM ref,
           substrate.physicality p
      JOIN substrate.entity ent ON ent.id = p.entity_id
      JOIN substrate.entity_type et ON et.id = ent.entity_type_id
     WHERE p.physicality_type_id = 13
       AND p.entity_id <> p_entity_id
       AND ST_FrechetDistance(ref.contour, p.geom) <= p_threshold
     ORDER BY frechet_distance
     LIMIT p_limit;
$$;

COMMENT ON FUNCTION substrate.similar_contours(BIGINT, FLOAT8, INT) IS
    'Find image contours within Frechet distance of a reference contour. Image contours are Mode-B physicality (X=pixel-x, Y=pixel-y, Z=edge-strength, M=contour-id-bitmask) per docs/specs/sql/mantissa-exploitation.md — Z and M are non-spatial payload, not metric. ST_FrechetDistance (2D, projects to X/Y) is the correct shape-similarity operator here. Reverted from migration 0054''s incorrect 4D switch.';
