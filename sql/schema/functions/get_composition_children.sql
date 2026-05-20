-- Walk a composition entity's children in canonical order.
--
-- The composition's physicality.geom is a LINESTRINGZM (or
-- MULTILINESTRINGZM) in either the 'entity' partition (entity-tier
-- compositions: word_form, grapheme_cluster, lemma, morpheme, ...) or
-- the 'content' partition (content-tier trajectories: text_composition,
-- paragraph, document, audio_chunk, pixel_region, video_frame). Both
-- partitions encode child identities via the substrate mantissa packing
-- contract:
--   X mantissa = child hash bits 0..51 (bb_pack_hash_lo)
--   Y mantissa = ordinal + RLE bit-banged (bb_pack_ordinal_rle)
--   Z mantissa = child hash bits 52..103 (bb_pack_hash_hi)
--   M mantissa = metadata (bb_pack_metadata; currently unused, reserved)
-- Reading the trajectory's vertices in order, unpacking via bb_unpack_*,
-- and joining against substrate.entity's composite btree on
-- (hash_bits_0_51, hash_bits_52_103) recovers the full child hash
-- sequence in one round trip — no junction table required.
--
-- A composition entity typically carries exactly one structural manifest
-- (in its tier's partition). If multiple physicality rows exist (e.g. an
-- atom-equivalent POINTZM stored alongside a structural LINESTRINGZM via
-- legacy decomposers), the manifest is selected by:
--   * mantissa-range filter: X > 2^51 retains mantissa-packed vertices and
--     excludes any real-coord POINTZM/LINESTRING dressed as composition
--   * vertex-count desc: pick the longest manifest (singletons
--     stored as doubled-vertex LINESTRINGs satisfy this too).
DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.get_composition_children(
    p_parent_hash substrate.hash_value
) RETURNS TABLE (ordinal INT, child_hash substrate.hash_value, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    -- Resolve the parent's expected child tier from its classification.
    -- For text: word_form → grapheme_cluster → codepoint;
    -- text_composition → word_form; paragraph → text_composition;
    -- document → paragraph. NULL = atom (no children).
    -- Disambiguates the singleton case where codepoint and grapheme_cluster
    -- share the same centroid (singleton grapheme = its single codepoint's
    -- coord) — without the tier filter, both match and the walk explodes.
    WITH parent_tier AS (
        SELECT et.code AS parent_code,
               CASE et.code
                   WHEN 'word_form'        THEN 'grapheme_cluster'
                   WHEN 'grapheme_cluster' THEN 'codepoint'
                   WHEN 'morpheme'         THEN 'grapheme_cluster'
                   WHEN 'lemma'            THEN 'word_form'
                   WHEN 'synset'           THEN 'lemma'
                   WHEN 'text_composition' THEN 'word_form'
                   WHEN 'paragraph'        THEN 'text_composition'
                   WHEN 'document'         THEN 'paragraph'
                   ELSE NULL
               END AS expected_child_code
          FROM substrate.entity_classification ec
          JOIN substrate.entity_type et ON et.id = ec.entity_type_id
         WHERE ec.entity_hash = p_parent_hash
         LIMIT 1
    ),
    composition_geom AS (
        SELECT p.geom, pt.code AS phys_code
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE p.entity_hash = p_parent_hash
           AND pt.code IN ('entity', 'content')
           AND GeometryType(p.geom) IN ('LINESTRING', 'MULTILINESTRING')
           AND ST_NumPoints(p.geom) >= 1
         ORDER BY ST_NumPoints(p.geom) DESC, p.content_hash
         LIMIT 1
    ),
    -- Singleton-doubled detection: PostGIS rejects single-vertex
    -- LINESTRINGs, so emitters pad k==1 by repeating the only vertex.
    -- When the geometry is exactly 2 vertices with identical coords,
    -- it represents ONE logical child. Cap the vertex iteration.
    geom_info AS (
        SELECT g.geom,
               g.phys_code,
               ST_NumPoints(g.geom) AS n,
               (
                   ST_NumPoints(g.geom) = 2 AND
                   ST_X(ST_PointN(g.geom, 1)) = ST_X(ST_PointN(g.geom, 2)) AND
                   ST_Y(ST_PointN(g.geom, 1)) = ST_Y(ST_PointN(g.geom, 2)) AND
                   ST_Z(ST_PointN(g.geom, 1)) = ST_Z(ST_PointN(g.geom, 2)) AND
                   ST_M(ST_PointN(g.geom, 1)) = ST_M(ST_PointN(g.geom, 2))
               ) AS is_singleton_doubled
          FROM composition_geom g
    ),
    vertices AS (
        SELECT idx.i AS vertex_idx, ST_PointN(g.geom, idx.i) AS v
          FROM geom_info g
          CROSS JOIN LATERAL generate_series(
              1,
              CASE WHEN g.is_singleton_doubled THEN 1 ELSE g.n END
          ) AS idx(i)
    ),
    classified AS (
        SELECT v.vertex_idx,
               ST_X(v.v) AS x, ST_Y(v.v) AS y, ST_Z(v.v) AS z, ST_M(v.v) AS m,
               (ST_X(v.v) > 2.0^51) AS is_mantissa
          FROM vertices v
    ),
    mantissa_resolved AS (
        SELECT substrate.bb_unpack_ordinal(c.y) AS ordinal,
               substrate.bb_unpack_rle(c.y)     AS rle_count,
               e.hash AS child_hash,
               c.vertex_idx
          FROM classified c
          JOIN substrate.entity e
            ON substrate.bb_hash_lo(e.hash)   = substrate.bb_unpack_hash_lo(c.x)
           AND substrate.bb_hash_hi(e.hash) = substrate.bb_unpack_hash_hi(c.z)
         WHERE c.is_mantissa
           AND EXISTS (
               SELECT 1
                 FROM substrate.entity_classification ec
                 JOIN substrate.entity_type et ON et.id = ec.entity_type_id
                 JOIN parent_tier pt ON pt.expected_child_code = et.code
                WHERE ec.entity_hash = e.hash
           )
    ),
    realcoord_resolved AS (
        -- Real-coord vertex reverse-resolve via substrate.physicality_entity
        -- (which holds the entity-tier POINTZM identity coords) replaces the
        -- pre-revert join on substrate.entity.centroid_* (those columns are
        -- gone — geometry lives only on substrate.physicality now).
        SELECT c.vertex_idx AS ordinal,
               1            AS rle_count,
               pe.entity_hash AS child_hash,
               c.vertex_idx
          FROM classified c
          JOIN substrate.physicality_entity pe
            ON ST_X(pe.geom) = c.x
           AND ST_Y(pe.geom) = c.y
           AND ST_Z(pe.geom) = c.z
           AND ST_M(pe.geom) = c.m
         WHERE NOT c.is_mantissa
           AND EXISTS (
               SELECT 1
                 FROM substrate.entity_classification ec
                 JOIN substrate.entity_type et ON et.id = ec.entity_type_id
                 JOIN parent_tier pt ON pt.expected_child_code = et.code
                WHERE ec.entity_hash = pe.entity_hash
           )
    )
    SELECT ordinal, child_hash, rle_count
      FROM (
        SELECT ordinal, child_hash, rle_count, vertex_idx FROM mantissa_resolved
        UNION ALL
        SELECT ordinal, child_hash, rle_count, vertex_idx FROM realcoord_resolved
      ) all_resolved
     ORDER BY ordinal, vertex_idx;
$f$;

COMMENT ON FUNCTION substrate.get_composition_children(substrate.hash_value) IS
    'Walk a composition entity''s children in canonical order by reading the LINESTRINGZM mantissa-packed vertices in physicality.geom (entity or content partition), unpacking child hash slices via bb_unpack_hash_lo/hi, and joining against substrate.entity''s composite btree on (hash_bits_0_51, hash_bits_52_103). No junction table — the geometry IS the relational structure.';
