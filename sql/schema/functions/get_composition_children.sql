-- Walk a composition entity's children in canonical order.
--
-- The composition's physicality.geom (physicality_type = 'contour') is a
-- LINESTRINGZM (or MULTILINESTRINGZM) whose vertices encode the children's
-- identities via the mantissa packing contract:
--   X mantissa = child hash bits 0..51 (bb_pack_hash_lo)
--   Y mantissa = ordinal + RLE bit-banged (bb_pack_ordinal_rle)
--   Z mantissa = child hash bits 52..103 (bb_pack_hash_hi)
--   M mantissa = metadata (bb_pack_metadata; currently unused, reserved)
-- Reading the trajectory's vertices in order, unpacking via bb_unpack_*,
-- and joining against substrate.entity's composite btree on
-- (hash_bits_0_51, hash_bits_52_103) recovers the full child hash sequence
-- in one round trip — no junction table required.
DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.get_composition_children(
    p_parent_hash substrate.hash_value
) RETURNS TABLE (ordinal INT, child_hash substrate.hash_value, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    WITH composition_geom AS (
        SELECT p.geom
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE p.entity_hash = p_parent_hash
           AND pt.code = 'contour'
         ORDER BY p.content_hash
         LIMIT 1
    ),
    vertices AS (
        SELECT idx.i AS vertex_idx,
               ST_PointN(g.geom, idx.i) AS v
          FROM composition_geom g
          CROSS JOIN LATERAL generate_series(1, ST_NumPoints(g.geom)) AS idx(i)
    ),
    unpacked AS (
        SELECT substrate.bb_unpack_ordinal(ST_Y(v.v)) AS ordinal,
               substrate.bb_unpack_rle(ST_Y(v.v))     AS rle_count,
               substrate.bb_unpack_hash_lo(ST_X(v.v)) AS hash_lo,
               substrate.bb_unpack_hash_hi(ST_Z(v.v)) AS hash_hi,
               v.vertex_idx
          FROM vertices v
    )
    SELECT u.ordinal, e.hash, u.rle_count
      FROM unpacked u
      JOIN substrate.entity e
        ON e.hash_bits_0_51   = u.hash_lo
       AND e.hash_bits_52_103 = u.hash_hi
     ORDER BY u.ordinal, u.vertex_idx;
$f$;

COMMENT ON FUNCTION substrate.get_composition_children(substrate.hash_value) IS
    'Walk a composition entity''s children in canonical order by reading the LINESTRINGZM mantissa-packed vertices in physicality.geom, unpacking child hash slices via bb_unpack_hash_lo/hi, and joining against substrate.entity''s composite btree on (hash_bits_0_51, hash_bits_52_103). No junction table — the geometry IS the relational structure.';
