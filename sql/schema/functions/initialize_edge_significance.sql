CREATE OR REPLACE FUNCTION substrate.initialize_edge_significance(
    p_context_code     TEXT,
    p_edge_type_code   TEXT,
    p_edge_hash        BYTEA,
    p_initial_mu       DOUBLE PRECISION
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id INT;
    v_edge_type_id INT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    SELECT id INTO v_edge_type_id
      FROM substrate.edge_type
     WHERE code = p_edge_type_code;
    IF v_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown edge_type: %', p_edge_type_code;
    END IF;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    VALUES
        (v_context_id, v_edge_type_id, p_edge_hash, p_initial_mu, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash)
    DO UPDATE SET mu = EXCLUDED.mu;
END $$;

COMMENT ON FUNCTION substrate.initialize_edge_significance(TEXT, TEXT, BYTEA, DOUBLE PRECISION) IS
    'Initialize or reset the mu value for one edge_significance row addressed by arena code and edge handle. Preserves sigma, volatility, and games on existing rows.';