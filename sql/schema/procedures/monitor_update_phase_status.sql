CREATE OR REPLACE PROCEDURE monitor.update_phase_status(
    p_phase_code    TEXT,
    p_status        TEXT,
    p_error_message TEXT DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO monitor.phase_status (phase_code, status, started_at, completed_at, error_message)
    VALUES (
        p_phase_code,
        p_status,
        CASE WHEN p_status IN ('started','running') THEN NOW() ELSE NULL END,
        CASE WHEN p_status IN ('completed','failed','skipped') THEN NOW() ELSE NULL END,
        p_error_message
    )
    ON CONFLICT (phase_code) DO UPDATE
        SET status        = EXCLUDED.status,
            started_at    = CASE
                                WHEN EXCLUDED.status IN ('started','running') THEN EXCLUDED.started_at
                                ELSE monitor.phase_status.started_at
                            END,
            completed_at  = CASE
                                WHEN EXCLUDED.status IN ('started','running') THEN NULL
                                ELSE EXCLUDED.completed_at
                            END,
            error_message = CASE
                                WHEN EXCLUDED.status IN ('started','running','completed') THEN NULL
                                ELSE EXCLUDED.error_message
                            END;
END $$;
COMMENT ON PROCEDURE monitor.update_phase_status(TEXT, TEXT, TEXT) IS
    'Upsert the last-known status of a phase. Status: running, completed, failed, skipped.';
