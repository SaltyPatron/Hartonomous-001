-- Returns the pass names that have completed for a given model_source. Used
-- by the IModelAnalysisPass orchestrator (Hartonomous.Decomposers.Safetensors)
-- to skip already-done work on resume.
--
-- Returns column is named pass_id for caller compatibility (the C# orchestrator
-- column-binds to "pass_id"); selected from the table's pass_name column.
CREATE OR REPLACE FUNCTION substrate.get_completed_model_passes(
    p_model_source_id BIGINT
) RETURNS TABLE (pass_id VARCHAR(64))
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT pass_name
      FROM substrate.model_pass_checkpoint
     WHERE model_source_id = p_model_source_id
       AND completed_at IS NOT NULL;
$$;

COMMENT ON FUNCTION substrate.get_completed_model_passes(BIGINT) IS
    'Returns the pass names that have completed for a given model_source. Used by the Safetensors pass orchestrator to skip already-done work on resume.';
