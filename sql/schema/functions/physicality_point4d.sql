-- Return the (x, y, z, m) coordinates of the first POINTZM physicality
-- attached to an entity classified as the requested type. Used by the
-- entity-info / inventory readers that want to extract the entity's atomic
-- real-coord centroid from its physicality row (for atoms — codepoint
-- S^3, audio sample, image pixel, etc.).
--
-- For composition entities, this function returns no row — their
-- physicality geom is LINESTRINGZM with mantissa-packed child refs, not
-- POINTZM. Composition representative POINTZMs are derived inline from
-- substrate.entity.hash_bits_0_51 / hash_bits_52_103 via
-- substrate.bb_pack_hash_lo / bb_pack_hash_hi when needed for edge.geom
-- construction (see substrate.populate_edge_trajectories) — they are not
-- stored anywhere.
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
       AND GeometryType(p.geom) = 'POINT'
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
    'Return x/y/z/m for the first deterministic POINTZM physicality on a hash classified as the requested entity type. For atom physicality only — compositions have ID-encoded LINESTRINGZM.';
