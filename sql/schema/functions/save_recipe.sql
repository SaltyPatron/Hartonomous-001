-- substrate.save_recipe — register a recipe as substrate content.
-- Computes BLAKE3 over the canonical JSON payload to derive the entity
-- hash (matches the C# ReferenceVocabularyHashes convention + the
-- substrate's general "identity is content hash" rule). Idempotent via
-- ON CONFLICT DO NOTHING — multiple ingests of the same model produce
-- the same recipe entity_hash, dedup at the substrate layer.
--
-- Inputs:
--   p_canonical_json BYTEA — the recipe's canonical-serialized JSON
--                            payload. Caller is responsible for
--                            canonicalization (sorted keys, no
--                            whitespace) so the hash is stable.
--   p_name           TEXT  — human-friendly identifier registered in
--                            substrate.recipe_name (NULL skips name
--                            registration; recipe still queryable by
--                            hash).
--   p_provenance     TEXT  — which provenance ingests this recipe
--                            (app-starter / per-model-source-code /
--                            user_session / etc.). Captured on the
--                            entity_classification row.
--   p_entity_type    TEXT  — defaults to 'recipe'. Allows specialization
--                            for sub-categories later if needed.
--
-- Returns the entity_hash of the recipe.
CREATE OR REPLACE FUNCTION substrate.save_recipe(
    p_canonical_json BYTEA,
    p_name           TEXT,
    p_provenance     TEXT,
    p_entity_type    TEXT DEFAULT 'recipe'
) RETURNS substrate.hash_value
LANGUAGE plpgsql
AS $$
DECLARE
    v_hash           substrate.hash_value;
    v_provenance_id  INT;
    v_entity_type_id INT;
BEGIN
    -- Content-address via BLAKE3. The hartonomous extension exposes
    -- blake3_hash(bytea) in the default extension schema (see line ~361
    -- of hartonomous--1.0.sql.in); same kernel C# Blake3.Hash32 calls.
    v_hash := blake3_hash(p_canonical_json)::substrate.hash_value;

    SELECT id INTO v_provenance_id  FROM substrate.provenance  WHERE code = p_provenance;
    IF v_provenance_id IS NULL THEN
        RAISE EXCEPTION 'save_recipe: unknown provenance code %', p_provenance;
    END IF;

    SELECT id INTO v_entity_type_id FROM substrate.entity_type WHERE code = p_entity_type;
    IF v_entity_type_id IS NULL THEN
        RAISE EXCEPTION 'save_recipe: unknown entity_type code %', p_entity_type;
    END IF;

    -- Substrate entity row (idempotent).
    INSERT INTO substrate.entity (hash) VALUES (v_hash)
    ON CONFLICT (hash) DO NOTHING;

    -- Classification under the requested provenance (idempotent).
    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    VALUES (v_hash, v_entity_type_id, v_provenance_id)
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    -- Payload.
    INSERT INTO substrate.recipe (entity_hash, canonical_json)
    VALUES (v_hash, p_canonical_json)
    ON CONFLICT (entity_hash) DO NOTHING;

    -- Optional name registration.
    IF p_name IS NOT NULL THEN
        INSERT INTO substrate.recipe_name (code, entity_hash)
        VALUES (p_name, v_hash)
        ON CONFLICT (code) DO UPDATE SET entity_hash = EXCLUDED.entity_hash;
    END IF;

    RETURN v_hash;
END $$;

COMMENT ON FUNCTION substrate.save_recipe(BYTEA, TEXT, TEXT, TEXT) IS
    'Register a recipe as content-addressed substrate content. Computes BLAKE3 over canonical JSON, INSERTs substrate.entity + substrate.entity_classification + substrate.recipe + (optional) substrate.recipe_name in one transaction. Idempotent — same canonical JSON → same row across ingests / practitioners. The substrate-layer dedup is the cross-source consensus surface for recipes.';
