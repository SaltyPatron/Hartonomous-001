CREATE OR REPLACE FUNCTION substrate.physicality_linestring4d(
    p_entity_hash substrate.hash_value,
    p_entity_type_code TEXT,
    p_physicality_type_code TEXT
) RETURNS DOUBLE PRECISION[]
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ARRAY(
        SELECT unnest(point4d_to_array(point_n(p.geom::linestring4d, i)))
          FROM generate_series(1, npoints(p.geom::linestring4d)) AS i
         ORDER BY i
    )
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE p.entity_hash = p_entity_hash
       AND pt.code = p_physicality_type_code
       AND ST_TypeTag4D(p.geom) = 2
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

COMMENT ON FUNCTION substrate.physicality_linestring4d(substrate.hash_value, TEXT, TEXT) IS
    'Return a flat x/y/z/m coordinate array for the first deterministic LINESTRING4D physicality attached to a hash classified as the requested entity type.';
