CREATE OR REPLACE FUNCTION substrate.reset_arena_priming_state()
RETURNS BIGINT
LANGUAGE sql VOLATILE
AS $$
    WITH reset_rows AS (
        UPDATE substrate.arena_priming_state
           SET last_edge_type_id = 0,
               last_hash = NULL,
               completed = FALSE,
               updated_at = now()
         RETURNING 1
    )
    SELECT count(*)::BIGINT FROM reset_rows;
$$;

COMMENT ON FUNCTION substrate.reset_arena_priming_state() IS
    'Reset per-arena significance-primer watermarks before a phase-owned priming pass. Re-scanning is idempotent via edge_significance ON CONFLICT and is required because later phases can add lower edge_type_id values.';
