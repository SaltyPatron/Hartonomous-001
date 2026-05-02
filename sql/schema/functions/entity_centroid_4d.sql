DROP FUNCTION IF EXISTS substrate.entity_centroid_4d(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.entity_centroid_4d(
    p_entity_hash BYTEA
) RETURNS geometry(GeometryZM)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT geom FROM substrate.physicality
     WHERE entity_hash = p_entity_hash
     ORDER BY physicality_type_id LIMIT 1;
$f$;
