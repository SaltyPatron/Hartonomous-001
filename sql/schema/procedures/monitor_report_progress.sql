CREATE OR REPLACE PROCEDURE monitor.report_progress(
    p_provenance_code TEXT,
    p_pass_name       TEXT,
    p_batch_number    INT,
    p_entities_total  BIGINT,
    p_edges_total     BIGINT,
    p_current_file    TEXT DEFAULT NULL,
    p_p1              TEXT DEFAULT NULL,  -- reserved
    p_p2              TEXT DEFAULT NULL,
    p_p3              TEXT DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO monitor.ingestion_progress
        (provenance_code, pass_name, batch_number, entities_total, edges_total, current_file)
    VALUES
        (p_provenance_code, p_pass_name, p_batch_number, p_entities_total, p_edges_total, p_current_file);
END $$;
COMMENT ON PROCEDURE monitor.report_progress(TEXT, TEXT, INT, BIGINT, BIGINT, TEXT, TEXT, TEXT, TEXT) IS
    'Append a per-batch ingestion-progress row.';
