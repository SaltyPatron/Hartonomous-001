-- substrate.get_recipe_by_hash(hash) → canonical_json BYTEA
-- Direct content-address lookup. Returns NULL if no row.
CREATE OR REPLACE FUNCTION substrate.get_recipe_by_hash(p_hash substrate.hash_value)
RETURNS BYTEA
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT canonical_json FROM substrate.recipe WHERE entity_hash = p_hash LIMIT 1
$$;

COMMENT ON FUNCTION substrate.get_recipe_by_hash(substrate.hash_value) IS
    'Resolve a recipe by its content-address hash. Returns canonical JSON BYTEA or NULL.';
