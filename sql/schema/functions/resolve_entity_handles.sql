DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[], TEXT[]);
DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.resolve_entity_handles(
    p_hashes BYTEA[], p_type_codes TEXT[]
) RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT et.code, e.hash
      FROM unnest(p_hashes) AS in_(h)
      JOIN substrate.entity e ON e.hash = in_.h
      JOIN substrate.entity_classification ec ON ec.entity_hash = e.hash
      JOIN substrate.entity_type et ON et.id = ec.entity_type_id
      JOIN unnest(p_type_codes) AS requested(code) ON requested.code = et.code
     GROUP BY et.code, e.hash
     ORDER BY et.code, e.hash;
$f$;
