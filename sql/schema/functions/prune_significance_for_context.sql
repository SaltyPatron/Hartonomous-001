CREATE OR REPLACE FUNCTION substrate.prune_significance_for_context(
    p_context_code TEXT,
    p_min_mu       DOUBLE PRECISION
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id INT;
    v_deleted BIGINT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    WITH deleted_edges AS (
        DELETE FROM substrate.edge_significance
         WHERE context_type_id = v_context_id
           AND mu < p_min_mu
         RETURNING 1
    ), deleted_entities AS (
        DELETE FROM substrate.entity_significance
         WHERE context_type_id = v_context_id
           AND mu < p_min_mu
         RETURNING 1
    )
    SELECT (SELECT count(*) FROM deleted_edges) +
           (SELECT count(*) FROM deleted_entities)
      INTO v_deleted;

    RETURN v_deleted;
END $$;

COMMENT ON FUNCTION substrate.prune_significance_for_context(TEXT, DOUBLE PRECISION) IS
    'Prune entity_significance and edge_significance rows below p_min_mu within one arena code. Returns total rows deleted across both substrate significance surfaces.';