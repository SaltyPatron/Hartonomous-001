-- substrate.claim_or_get_embedding_anchor(p_model_source_id, p_intersection_count)
--
-- Atomic anchor selection for cross-model embedding alignment. Returns the
-- existing anchor's model_source_id if any; otherwise claims the supplied
-- model as the canonical anchor (first-write-wins via ON CONFLICT). The
-- caller (EmbeddingAlignmentPass) compares the returned id with its own
-- to decide whether to skip alignment (it IS the anchor) or proceed
-- (Procrustes-fit a rotation against the anchor).

CREATE OR REPLACE FUNCTION substrate.claim_or_get_embedding_anchor(
    p_model_source_id    INT,
    p_intersection_count INT
) RETURNS INT
LANGUAGE SQL
VOLATILE
AS $$
    INSERT INTO substrate.embedding_alignment_anchor
        (model_source_id, vocab_intersection_token_count)
    VALUES
        (p_model_source_id, p_intersection_count)
    ON CONFLICT (model_source_id) DO NOTHING;

    SELECT model_source_id
      FROM substrate.embedding_alignment_anchor
     ORDER BY set_at ASC
     LIMIT 1;
$$;

COMMENT ON FUNCTION substrate.claim_or_get_embedding_anchor(INT, INT) IS
    'Returns current canonical embedding anchor''s model_source_id (first-write-wins). Atomic via ON CONFLICT DO NOTHING. Used by EmbeddingAlignmentPass to decide anchor-vs-aligner role.';
