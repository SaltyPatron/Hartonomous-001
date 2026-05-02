-- Reverse 0022: restore the original buggy upsert_model_pass_checkpoint body
-- that gated started_at on p_status = 'started'.
--
-- WARNING: under this body the function violates the NOT NULL constraint on
-- started_at on every first INSERT, because the only caller never sends
-- p_status='started'. Restoration is for migration-history fidelity only.

CREATE OR REPLACE FUNCTION substrate.upsert_model_pass_checkpoint(
    p_model_source_id INT,
    p_pass_name       TEXT,
    p_status          TEXT,
    p_rows_emitted    BIGINT,
    p_error_message   TEXT,
    p_extra           JSONB DEFAULT NULL
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    PERFORM p_extra;
    INSERT INTO substrate.model_pass_checkpoint
        (model_source_id, pass_name, started_at, completed_at, rows_emitted, error_message)
    VALUES (
        p_model_source_id,
        p_pass_name,
        CASE WHEN p_status = 'started'   THEN NOW() ELSE NULL END,
        CASE WHEN p_status = 'completed' THEN NOW() ELSE NULL END,
        COALESCE(p_rows_emitted, 0),
        p_error_message
    )
    ON CONFLICT (model_source_id, pass_name) DO UPDATE
        SET started_at    = COALESCE(substrate.model_pass_checkpoint.started_at, EXCLUDED.started_at),
            completed_at  = EXCLUDED.completed_at,
            rows_emitted  = EXCLUDED.rows_emitted,
            error_message = EXCLUDED.error_message
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;
