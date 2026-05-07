CREATE OR REPLACE FUNCTION monitor.close_session()
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_rows INT;
BEGIN
    UPDATE monitor.session
       SET ended_at = NOW()
     WHERE ended_at IS NULL
       AND started_at = (SELECT MAX(started_at) FROM monitor.session WHERE ended_at IS NULL);

  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN v_rows > 0;
END $$;

COMMENT ON FUNCTION monitor.close_session() IS
  'Close the most recent open session and return true when a row was closed.';
