CREATE OR REPLACE FUNCTION substrate.physicality_point4d(
    p_entity_hash substrate.hash_value,
    p_entity_type_code TEXT,
    p_physicality_type_code TEXT
) RETURNS TABLE (x1 DOUBLE PRECISION, x2 DOUBLE PRECISION, x3 DOUBLE PRECISION, x4 DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ST_X(p.geom), ST_Y(p.geom), ST_Z(p.geom), ST_M(p.geom)
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE p.entity_hash = p_entity_hash
       AND pt.code = p_physicality_type_code
       AND ST_GeometryType(p.geom) = 'ST_Point'
       AND ST_NDims(p.geom) = 4
       AND EXISTS (
           SELECT 1
             FROM substrate.entity_classification ec
             JOIN substrate.entity_type et ON et.id = ec.entity_type_id
            WHERE ec.entity_hash = p.entity_hash
              AND et.code = p_entity_type_code
       )
     ORDER BY p.content_hash
     LIMIT 1;
$f$;

COMMENT ON FUNCTION substrate.physicality_point4d(substrate.hash_value, TEXT, TEXT) IS
    'Return x/y/z/m coordinates for the first deterministic POINTZM physicality attached to a hash classified as the requested entity type.';
