CREATE OR REPLACE PROCEDURE monitor.archive_session(p_session_id UUID)
LANGUAGE plpgsql
AS $$
BEGIN
    -- Archival is currently a no-op; the session row stays in monitor.session
    -- with ended_at populated by close_session. This procedure exists so the
    -- C# CLI's session management surface has somewhere to call.
    UPDATE monitor.session SET ended_at = COALESCE(ended_at, NOW())
     WHERE id = p_session_id;
END $$;
COMMENT ON PROCEDURE monitor.archive_session(UUID) IS
    'Mark a session as ended (idempotent). Future revisions may move rows to a cold-storage table.';
