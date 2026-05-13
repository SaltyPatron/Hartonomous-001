-- Return a flat (x1, x2, x3, x4, x1, x2, x3, x4, ...) coordinate array for
-- the first deterministic LINESTRINGZM physicality on an entity. For
-- composition physicality this returns the mantissa-packed vertex
-- coordinates — callers iterating this should unpack via bb_unpack_*
-- helpers (X = child hash bits 0..51, Y = ordinal+RLE, Z = child hash bits
-- 52..103, M = metadata) rather than treating the values as metric coords.
CREATE OR REPLACE FUNCTION substrate.physicality_linestring4d(
    p_entity_hash substrate.hash_value,
    p_entity_type_code TEXT,
    p_physicality_type_code TEXT
) RETURNS DOUBLE PRECISION[]
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ARRAY(
        SELECT unnest(ARRAY[ST_X(v), ST_Y(v), ST_Z(v), ST_M(v)])
          FROM generate_series(1, ST_NumPoints(p.geom)) AS idx(i)
          CROSS JOIN LATERAL (SELECT ST_PointN(p.geom, idx.i) AS v) pt
         ORDER BY idx.i
    )
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE p.entity_hash = p_entity_hash
       AND pt.code = p_physicality_type_code
       AND GeometryType(p.geom) = 'LINESTRING'
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

COMMENT ON FUNCTION substrate.physicality_linestring4d(substrate.hash_value, TEXT, TEXT) IS
    'Flat coordinate array for the first deterministic LINESTRINGZM physicality. For composition physicality this returns mantissa-packed vertices — callers unpack via bb_unpack_* (X = hash_lo, Y = ordinal+RLE, Z = hash_hi, M = metadata).';
