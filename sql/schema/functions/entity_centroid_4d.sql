DROP FUNCTION IF EXISTS substrate.entity_centroid_4d(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.entity_centroid_4d(
    p_entity_hash BYTEA
) RETURNS point4d
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT substrate.geometry4d_centroid(geom)
     FROM substrate.physicality
     WHERE entity_hash = p_entity_hash
     ORDER BY physicality_type_id LIMIT 1;
$f$;
