CREATE OR REPLACE FUNCTION monitor.create_session(
    p_label TEXT,
    p_notes TEXT DEFAULT NULL
) RETURNS UUID
LANGUAGE plpgsql
AS $$
DECLARE
    v_id UUID := gen_random_uuid();
BEGIN
    INSERT INTO monitor.session (id, user_label, started_at, notes)
    VALUES (v_id, p_label, NOW(), p_notes);
    RETURN v_id;
END $$;
COMMENT ON FUNCTION monitor.create_session(TEXT, TEXT) IS
    'Open a new monitor.session row and return its UUID.';
