-- substrate.entity_centroid_4d(entity_type_id, entity_hash)
--
-- Returns a representative 4D centroid POINTZM for the given entity by
-- consulting substrate.physicality:
--
--   1. If the entity has an s3_position POINTZM, that point IS the centroid.
--   2. Else if the entity has any other POINTZM physicality (hilbert_value,
--      single-point weight_distribution, etc.), use that point.
--   3. Else if the entity has a contour or other LINESTRINGZM physicality,
--      compute the arithmetic mean of vertex coordinates as a POINTZM.
--   4. Else NULL.
--
-- Per the recursive-centroid law in .claude/rules/25-physicality-4d.md the
-- canonical centroid for a composition entity is its LINESTRINGZM mean,
-- equivalent to the result branch (3) yields. Atom entities sourced from
-- the codepoint partition land on branch (1) directly.
--
-- The function is STABLE PARALLEL SAFE — pure read of substrate.physicality.
CREATE OR REPLACE FUNCTION substrate.entity_centroid_4d(
    p_entity_type_id INT,
    p_entity_hash    BYTEA
)
RETURNS geometry
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH candidates AS (
        SELECT p.geom, pt.code AS phys_code,
               ST_GeometryType(p.geom) AS geom_type,
               CASE pt.code
                   WHEN 's3_position'   THEN 1
                   WHEN 'hilbert_value' THEN 2
                   WHEN 'contour'       THEN 3
                   ELSE 99
               END AS preference
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE p.entity_type_id = p_entity_type_id
           AND p.entity_hash    = p_entity_hash
    ),
    chosen AS (
        SELECT geom, geom_type
          FROM candidates
         ORDER BY preference, geom_type
         LIMIT 1
    )
    SELECT
        CASE
            WHEN c.geom_type = 'ST_Point' THEN c.geom
            WHEN c.geom_type IN ('ST_LineString', 'ST_MultiLineString') THEN
                ST_MakePoint(
                    (SELECT avg(ST_X(d.geom)) FROM ST_DumpPoints(c.geom) AS d),
                    (SELECT avg(ST_Y(d.geom)) FROM ST_DumpPoints(c.geom) AS d),
                    (SELECT avg(ST_Z(d.geom)) FROM ST_DumpPoints(c.geom) AS d),
                    (SELECT avg(ST_M(d.geom)) FROM ST_DumpPoints(c.geom) AS d)
                )
            ELSE NULL
        END
    FROM chosen c;
$$;

COMMENT ON FUNCTION substrate.entity_centroid_4d(INT, BYTEA) IS
    'Representative 4D centroid POINTZM for a composite-handle entity. Prefers s3_position, falls back to other POINTZM, then mean of LINESTRINGZM vertices.';
