-- 0055_similar_contours_2d_revert.down.sql
-- Re-applies migration 0054's (incorrect) 4D version of similar_contours.
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
