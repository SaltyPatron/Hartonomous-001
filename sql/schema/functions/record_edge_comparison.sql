CREATE OR REPLACE FUNCTION substrate.record_edge_comparison(
    p_context_code          TEXT,
    p_winner_edge_type_code TEXT,
    p_winner_edge_hash      BYTEA,
    p_loser_edge_type_code  TEXT,
    p_loser_edge_hash       BYTEA
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id INT;
    v_winner_edge_type_id INT;
    v_loser_edge_type_id INT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    SELECT id INTO v_winner_edge_type_id
      FROM substrate.edge_type
     WHERE code = p_winner_edge_type_code;
    IF v_winner_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown winner edge_type: %', p_winner_edge_type_code;
    END IF;

    SELECT id INTO v_loser_edge_type_id
      FROM substrate.edge_type
     WHERE code = p_loser_edge_type_code;
    IF v_loser_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown loser edge_type: %', p_loser_edge_type_code;
    END IF;

    PERFORM substrate.record_comparison(
        v_context_id,
        v_winner_edge_type_id,
        p_winner_edge_hash,
        v_loser_edge_type_id,
        p_loser_edge_hash);
END $$;

COMMENT ON FUNCTION substrate.record_edge_comparison(TEXT, TEXT, BYTEA, TEXT, BYTEA) IS
    'Resolve arena and edge type codes, then record a Glicko-2 head-to-head update on substrate.edge_significance for winner/loser edge handles.';