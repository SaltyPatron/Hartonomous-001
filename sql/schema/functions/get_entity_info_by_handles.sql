DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(INT[], BYTEA[]);
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_entity_info_by_handles(
    p_hashes BYTEA[]
) RETURNS TABLE (entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash FROM unnest(p_hashes) AS in_(h) JOIN substrate.entity e ON e.hash = in_.h;
$f$;
