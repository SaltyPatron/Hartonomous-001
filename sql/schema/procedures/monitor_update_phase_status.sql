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
        CASE WHEN p_status = 'started' THEN NOW() ELSE NULL END,
        CASE WHEN p_status IN ('completed','failed','skipped') THEN NOW() ELSE NULL END,
        p_error_message
    )
    ON CONFLICT (phase_code) DO UPDATE
        SET status        = EXCLUDED.status,
            started_at    = COALESCE(monitor.phase_status.started_at, EXCLUDED.started_at),
            completed_at  = EXCLUDED.completed_at,
            error_message = EXCLUDED.error_message;
END $$;
COMMENT ON PROCEDURE monitor.update_phase_status(TEXT, TEXT, TEXT) IS
    'Upsert the last-known status of a phase. Status: started, completed, failed, skipped.';
