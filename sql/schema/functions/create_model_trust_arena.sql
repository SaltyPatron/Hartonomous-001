-- substrate.create_model_trust_arena(model_provenance_code TEXT)
--
-- Convenience: creates the per-model trust arena `model_trust:<provenance>`
-- when a model is ingested. Wraps substrate.create_arena with the canonical
-- naming convention. Returns the arena id.
CREATE OR REPLACE FUNCTION substrate.create_model_trust_arena(
    p_model_provenance_code TEXT
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_arena_code TEXT;
BEGIN
    IF p_model_provenance_code IS NULL OR length(trim(p_model_provenance_code)) = 0 THEN
        RAISE EXCEPTION 'p_model_provenance_code must be a non-empty provenance code';
    END IF;

    v_arena_code := 'model_trust:' || p_model_provenance_code;
    RETURN substrate.create_arena(v_arena_code, TRUE);
END $$;

COMMENT ON FUNCTION substrate.create_model_trust_arena(TEXT) IS
    'Create per-model trust arena `model_trust:<provenance>` for an ingested model. Backfills against existing edges. Idempotent.';
