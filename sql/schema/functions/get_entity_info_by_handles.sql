DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(INT[], BYTEA[]);
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_entity_info_by_handles(
    p_type_codes TEXT[], p_hashes BYTEA[]
) RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT requested.type_code, e.hash
      FROM unnest(p_type_codes, p_hashes) AS requested(type_code, h)
      JOIN substrate.entity e ON e.hash = requested.h
      JOIN substrate.entity_type et ON et.code = requested.type_code
      JOIN substrate.entity_classification ec
        ON ec.entity_hash = e.hash
       AND ec.entity_type_id = et.id
     GROUP BY requested.type_code, e.hash
     ORDER BY requested.type_code, e.hash;
$f$;
