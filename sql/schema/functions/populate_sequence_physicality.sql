-- Populate missing composition physicality from existing sequence child centroids.
CREATE OR REPLACE FUNCTION substrate.populate_sequence_physicality(p_limit INT)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_inserted BIGINT;
BEGIN
    WITH candidate_parents AS (
        SELECT s.parent_hash
          FROM substrate.sequence s
         WHERE NOT EXISTS (
                   SELECT 1
                     FROM substrate.physicality existing
                    WHERE existing.entity_hash = s.parent_hash)
         GROUP BY s.parent_hash
        HAVING count(*) >= 1
           AND count(substrate.geom_to_pointzm(substrate.entity_centroid_4d(s.child_hash))) = count(*)
         ORDER BY s.parent_hash
         LIMIT p_limit
    ), child_points AS (
        SELECT s.parent_hash,
               s.ordinal,
               s.child_hash,
               substrate.geom_to_pointzm(substrate.entity_centroid_4d(s.child_hash)) AS point_geom
          FROM substrate.sequence s
          JOIN candidate_parents c ON c.parent_hash = s.parent_hash
    ), assembled AS (
        SELECT parent_hash,
               count(*) AS child_count,
               (array_agg(point_geom ORDER BY ordinal, child_hash))[1] AS first_point,
               ST_MakeLine(point_geom ORDER BY ordinal, child_hash) AS line_geom
          FROM child_points
         GROUP BY parent_hash
    ), rows_to_insert AS (
        SELECT CASE WHEN a.child_count = 1 THEN s3.id ELSE contour.id END AS physicality_type_id,
               a.parent_hash AS entity_hash,
               a.parent_hash AS content_hash,
               CASE WHEN a.child_count = 1 THEN a.first_point ELSE a.line_geom END AS geom
          FROM assembled a
          JOIN substrate.physicality_type s3 ON s3.code = 's3_position'
          JOIN substrate.physicality_type contour ON contour.code = 'contour'
    )
    INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
    SELECT physicality_type_id, entity_hash, content_hash, geom
      FROM rows_to_insert
     WHERE geom IS NOT NULL
    ON CONFLICT (physicality_type_id, entity_hash, content_hash) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END $$;

COMMENT ON FUNCTION substrate.populate_sequence_physicality(INT) IS
    'Populate missing entity physicality from substrate.sequence child centroids: singleton compositions receive POINTZM s3_position, multi-child compositions receive contour LINESTRINGZM.';
