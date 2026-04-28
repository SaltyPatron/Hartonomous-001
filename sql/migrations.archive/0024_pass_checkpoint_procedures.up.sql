-- 0024_pass_checkpoint_procedures.up.sql
-- Stored functions used by the IModelAnalysisPass orchestrator.
--
-- The model_pass_checkpoint TABLE itself was created in migration 0020 alongside
-- the rest of model_source identity. This migration only adds the upsert and
-- query functions the C# orchestrator calls — DDL stays in 0020, DML logic
-- stays here, no inline SQL leaks into the C# call sites.
--
-- Lifecycle:
--   * orchestrator starts a pass     → upsert with completed=false (clears last_error,
--                                       resets completed_at to NULL on retry)
--   * pass succeeds                  → upsert with completed=true (stamps completed_at)
--   * pass throws                    → upsert with completed=false + last_error text
--   * orchestrator queries completed → get_completed_model_passes() returns pass_ids
--                                       to skip on resume

CREATE OR REPLACE FUNCTION substrate.upsert_model_pass_checkpoint(
    p_model_source_id BIGINT,
    p_pass_id         VARCHAR(64),
    p_entity_count    BIGINT,
    p_edge_count      BIGINT,
    p_last_error      TEXT,
    p_completed       BOOLEAN
) RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_id BIGINT;
BEGIN
    INSERT INTO substrate.model_pass_checkpoint
        (model_source_id, pass_id, entity_count, edge_count, last_error, completed_at)
    VALUES
        (p_model_source_id, p_pass_id, p_entity_count, p_edge_count, p_last_error,
         CASE WHEN p_completed THEN now() ELSE NULL END)
    ON CONFLICT (model_source_id, pass_id) DO UPDATE
       SET entity_count = EXCLUDED.entity_count,
           edge_count   = EXCLUDED.edge_count,
           last_error   = EXCLUDED.last_error,
           -- Stamp completion only on success. A retry that starts (completed=false)
           -- clears the prior completed_at so the orchestrator sees the pass as in-flight.
           completed_at = CASE WHEN p_completed THEN now() ELSE NULL END
    RETURNING id INTO v_id;
    RETURN v_id;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.get_completed_model_passes(
    p_model_source_id BIGINT
) RETURNS TABLE (pass_id VARCHAR(64))
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT pass_id
      FROM substrate.model_pass_checkpoint
     WHERE model_source_id = p_model_source_id
       AND completed_at IS NOT NULL;
$$;

COMMENT ON FUNCTION substrate.upsert_model_pass_checkpoint(BIGINT, VARCHAR, BIGINT, BIGINT, TEXT, BOOLEAN) IS
    'Records IModelAnalysisPass progress. completed=true stamps completed_at; completed=false clears it (retry semantics).';
COMMENT ON FUNCTION substrate.get_completed_model_passes(BIGINT) IS
    'Returns the pass_ids that have completed for a given model_source. Used by the orchestrator to skip already-done work on resume.';
