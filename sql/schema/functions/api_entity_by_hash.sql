CREATE OR REPLACE FUNCTION substrate.api_entity_by_hash(
    p_entity_hash BYTEA
) RETURNS TABLE (entity_hash BYTEA, classifications JSONB)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash, substrate.api_entity_classifications(e.hash)
      FROM substrate.entity e
     WHERE e.hash = p_entity_hash;
$f$;