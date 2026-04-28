CREATE OR REPLACE FUNCTION monitor.close_session()
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE monitor.session
       SET ended_at = NOW()
     WHERE ended_at IS NULL
       AND started_at = (SELECT MAX(started_at) FROM monitor.session WHERE ended_at IS NULL);
END $$;
COMMENT ON FUNCTION monitor.close_session() IS
    'Close the most recent open session.';
