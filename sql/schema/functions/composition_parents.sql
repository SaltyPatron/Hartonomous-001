-- composition_parents(child_hash) — reverse lookup: find every composition
-- whose trajectory contains p_child_hash as a child at some position.
--
-- Implementation: extract the child's 104-bit hash prefix (hash_bits_0_51,
-- hash_bits_52_103), then for every composition physicality (type 'contour')
-- iterate its LINESTRINGZM vertices via ST_PointN, unpacking vertex X and Z
-- mantissas via bb_unpack_hash_lo / bb_unpack_hash_hi; report parent rows
-- where any vertex's (lo, hi) matches the child's (lo, hi).
--
-- NOTE: this implementation walks every composition's geometry sequentially
-- for the linear-scan version of S3.D. The follow-up native fast path
-- (libhartonomous lh_trajectory_unpack + pg_trajectory_walk SRFs) replaces
-- this with a C-kernel-driven extraction + spatial index. Until then this
-- correctly answers reverse-parent queries but does not scale to huge
-- physicality tables; use sparingly until the native fast path lands.
DROP FUNCTION IF EXISTS substrate.composition_parents(INT, BYTEA);
DROP FUNCTION IF EXISTS substrate.composition_parents(BYTEA);
CREATE OR REPLACE FUNCTION substrate.composition_parents(
    p_child_hash substrate.hash_value
) RETURNS TABLE (parent_hash substrate.hash_value, ordinal INT, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    WITH child_prefix AS (
        SELECT substrate.bb_hash_lo(p_child_hash) AS lo,
               substrate.bb_hash_hi(p_child_hash) AS hi
    ),
    composition_geoms AS (
        SELECT p.entity_hash, p.geom
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE pt.code = 'contour'
    ),
    vertices AS (
        SELECT g.entity_hash,
               ST_PointN(g.geom, idx.i) AS v
          FROM composition_geoms g
          CROSS JOIN LATERAL generate_series(1, ST_NumPoints(g.geom)) AS idx(i)
    ),
    unpacked AS (
        SELECT v.entity_hash AS parent_hash,
               substrate.bb_unpack_ordinal(ST_Y(v.v))  AS ordinal,
               substrate.bb_unpack_rle(ST_Y(v.v))      AS rle_count,
               substrate.bb_unpack_hash_lo(ST_X(v.v))  AS hash_lo,
               substrate.bb_unpack_hash_hi(ST_Z(v.v))  AS hash_hi
          FROM vertices v
    )
    SELECT u.parent_hash, u.ordinal, u.rle_count
      FROM unpacked u
      CROSS JOIN child_prefix cp
     WHERE u.hash_lo = cp.lo
       AND u.hash_hi = cp.hi
     ORDER BY u.parent_hash, u.ordinal;
$f$;

COMMENT ON FUNCTION substrate.composition_parents(substrate.hash_value) IS
    'Reverse lookup: every composition whose LINESTRINGZM trajectory contains p_child_hash as a child. Sequential scan version (linear-scan); native fast-path SRF replaces this in the follow-up S3 work.';
