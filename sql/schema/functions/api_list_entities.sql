CREATE OR REPLACE FUNCTION substrate.api_list_entities(
    p_entity_type_code TEXT DEFAULT NULL,
    p_after_hash BYTEA DEFAULT NULL,
    p_limit INT DEFAULT 100
) RETURNS TABLE (entity_hash BYTEA, classifications JSONB)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash, substrate.api_entity_classifications(e.hash)
      FROM substrate.entity e
     WHERE (p_after_hash IS NULL OR e.hash > p_after_hash)
       AND (
           p_entity_type_code IS NULL
           OR EXISTS (
               SELECT 1
                 FROM substrate.entity_classification ec
                 JOIN substrate.entity_type et ON et.id = ec.entity_type_id
                WHERE ec.entity_hash = e.hash
                  AND et.code = p_entity_type_code
           )
       )
     ORDER BY e.hash
     LIMIT LEAST(GREATEST(COALESCE(p_limit, 100), 1), 1000);
$f$;