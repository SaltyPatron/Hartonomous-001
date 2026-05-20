-- substrate.list_recipes() — enumerate all registered recipes with their
-- names + provenances. Used by the practitioner CLI for "hart recipe list".
CREATE OR REPLACE FUNCTION substrate.list_recipes()
RETURNS TABLE (
    code        TEXT,
    entity_hash substrate.hash_value,
    provenance  TEXT,
    bytes       INT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT rn.code, rn.entity_hash, p.code AS provenance, octet_length(r.canonical_json) AS bytes
      FROM substrate.recipe r
      JOIN substrate.recipe_name rn ON rn.entity_hash = r.entity_hash
      JOIN substrate.entity_classification ec ON ec.entity_hash = r.entity_hash
      JOIN substrate.provenance p ON p.id = ec.provenance_id
      JOIN substrate.entity_type et ON et.id = ec.entity_type_id AND et.code = 'recipe'
     ORDER BY rn.code
$$;

COMMENT ON FUNCTION substrate.list_recipes() IS
    'Enumerate all registered recipes: name + content hash + provenance tier + payload size. Powers "hart recipe list" CLI.';
