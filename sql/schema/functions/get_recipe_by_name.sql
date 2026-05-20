-- substrate.get_recipe_by_name(name) → canonical_json BYTEA
-- Resolves a human-friendly recipe code (e.g. 'minilm-base',
-- 'qwen-2.5-coder-3b', or a per-model-source ingest-emitted code) to the
-- recipe's canonical JSON payload. Returns NULL if no row.
CREATE OR REPLACE FUNCTION substrate.get_recipe_by_name(p_name TEXT)
RETURNS BYTEA
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT r.canonical_json
      FROM substrate.recipe_name rn
      JOIN substrate.recipe r ON r.entity_hash = rn.entity_hash
     WHERE rn.code = p_name
     LIMIT 1
$$;

COMMENT ON FUNCTION substrate.get_recipe_by_name(TEXT) IS
    'Resolve a recipe by its human-friendly code (registered via substrate.recipe_name). Returns the canonical JSON payload as BYTEA, or NULL if no match. Used by the synth CLI to translate --recipe-name to the actual recipe content.';
