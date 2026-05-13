-- Return an entity's universal 4D representative POINTZM. Reads the
-- entity.centroid_4d column directly — the ingestion pipeline populates it
-- with the entity's real-coord position (atoms: content-derived; compositions:
-- recursive mean of children's centroid_4d). Previous version walked
-- substrate.physicality which broke under the corrected model where
-- composition physicality.geom is ID-encoded LINESTRINGZM (not real coords).
DROP FUNCTION IF EXISTS substrate.entity_centroid_4d(INT, BYTEA);
DROP FUNCTION IF EXISTS substrate.entity_centroid_4d(BYTEA);
CREATE OR REPLACE FUNCTION substrate.entity_centroid_4d(
    p_entity_hash substrate.hash_value
) RETURNS public.point4d
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT centroid_4d::public.point4d
      FROM substrate.entity
     WHERE hash = p_entity_hash;
$f$;

COMMENT ON FUNCTION substrate.entity_centroid_4d(substrate.hash_value) IS
    'Universal 4D representative POINTZM for an entity. Reads substrate.entity.centroid_4d (real-coord) directly. NOT computed from physicality.geom because composition physicality is ID-encoded.';
