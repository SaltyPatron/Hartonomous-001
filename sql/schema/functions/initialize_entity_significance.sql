CREATE OR REPLACE FUNCTION substrate.initialize_entity_significance(
    p_context_code          TEXT,
    p_entity_hash           BYTEA,
    p_initial_mu            DOUBLE PRECISION,
    p_attestation_type_code TEXT DEFAULT 'positive_evidence'
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id          INT;
    v_attestation_type_id INT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    INSERT INTO substrate.entity_significance
        (context_type_id, entity_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (v_context_id, p_entity_hash, v_attestation_type_id,
         p_initial_mu, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, entity_hash, attestation_type_id)
    DO UPDATE SET mu = EXCLUDED.mu;
END $$;

COMMENT ON FUNCTION substrate.initialize_entity_significance(TEXT, BYTEA, DOUBLE PRECISION, TEXT) IS
    'Initialize or reset the mu value for one entity_significance row addressed by (arena, entity, attestation_type). Default attestation_type is positive_evidence — ingestion-time priming. Preserves sigma, volatility, and games on existing rows.';
