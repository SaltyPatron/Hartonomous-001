CREATE OR REPLACE FUNCTION substrate.upsert_model_pass_checkpoint(
    p_model_source_id INT,
    p_pass_name       TEXT,
    p_status          TEXT,        -- "in_flight" | "completed" | "failed"
    p_rows_emitted    BIGINT,
    p_error_message   TEXT,
    p_extra           JSONB DEFAULT NULL
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    -- p_extra reserved for future per-pass payload; current schema doesn't use it.
    PERFORM p_extra;
    -- INSERT branch only fires when there is no existing row for this
    -- (model_source_id, pass_name) — i.e., the pass is being observed for
    -- the first time. By definition that IS the start, so started_at is
    -- always NOW(). The previous CASE-on-status form gated started_at on
    -- a 'started' status the producer (NpgsqlCheckpointStore) never sends,
    -- which violated the NOT NULL constraint on first-batch upserts.
    INSERT INTO substrate.model_pass_checkpoint
        (model_source_id, pass_name, started_at, completed_at, rows_emitted, error_message)
    VALUES (
        p_model_source_id,
        p_pass_name,
        NOW(),
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
