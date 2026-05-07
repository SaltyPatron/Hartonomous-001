CREATE OR REPLACE PROCEDURE monitor.reset_phase_checkpoint(p_phase_code TEXT)
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM monitor.phase_status WHERE phase_code = p_phase_code;
    TRUNCATE TABLE substrate.model_pass_checkpoint;
END $$;

COMMENT ON PROCEDURE monitor.reset_phase_checkpoint(TEXT) IS
    'Reset a phase status row and clear model pass checkpoints for CLI phase reruns.';