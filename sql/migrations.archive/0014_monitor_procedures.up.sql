-- 0014_monitor_procedures.up.sql
-- Monitor stored procedures per specs/operations/monitoring.md and sessions.md.

-- Report batch progress (called by decomposer after each batch)
CREATE OR REPLACE PROCEDURE monitor.report_progress(
    p_decomposer_code   TEXT,
    p_phase_code        TEXT,
    p_batch_number      INT,
    p_entities_ingested BIGINT DEFAULT 0,
    p_edges_created     BIGINT DEFAULT 0,
    p_junctions_created BIGINT DEFAULT 0,
    p_status            TEXT DEFAULT 'completed',
    p_error_message     TEXT DEFAULT NULL,
    p_error_context     JSONB DEFAULT NULL
)
LANGUAGE plpgsql AS $$
DECLARE
    v_progress_id BIGINT;
BEGIN
    INSERT INTO monitor.ingestion_progress (
        decomposer_code, phase_code, batch_number,
        entities_ingested, edges_created, junctions_created,
        status, completed_at, error_message, error_context
    ) VALUES (
        p_decomposer_code, p_phase_code, p_batch_number,
        p_entities_ingested, p_edges_created, p_junctions_created,
        p_status,
        CASE WHEN p_status IN ('completed', 'failed') THEN now() ELSE NULL END,
        p_error_message, p_error_context
    ) RETURNING progress_id INTO v_progress_id;

    IF p_status = 'failed' AND p_error_message IS NOT NULL THEN
        INSERT INTO monitor.error_log (
            decomposer_code, phase_code, category, message, context
        ) VALUES (
            p_decomposer_code, p_phase_code, 'ingestion_error', p_error_message, p_error_context
        );
    END IF;
END;
$$;

-- Log error directly
CREATE OR REPLACE PROCEDURE monitor.log_error(
    p_category          TEXT,
    p_message           TEXT,
    p_decomposer_code   TEXT DEFAULT NULL,
    p_phase_code        TEXT DEFAULT NULL,
    p_entity_hash       BYTEA DEFAULT NULL,
    p_source_file       TEXT DEFAULT NULL,
    p_source_line       INT DEFAULT NULL,
    p_context           JSONB DEFAULT NULL
)
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO monitor.error_log (
        category, message, decomposer_code, phase_code,
        entity_hash, source_file, source_line, context
    ) VALUES (
        p_category, p_message, p_decomposer_code, p_phase_code,
        p_entity_hash, p_source_file, p_source_line, p_context
    );
END;
$$;

-- Snapshot substrate health (queries pg_stat tables)
CREATE OR REPLACE PROCEDURE monitor.snapshot_health()
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO monitor.substrate_health (table_schema, table_name, row_count, dead_tuples, disk_bytes, index_bytes)
    SELECT
        n.nspname,
        c.relname,
        COALESCE(s.n_live_tup, 0),
        COALESCE(s.n_dead_tup, 0),
        pg_relation_size(c.oid),
        pg_indexes_size(c.oid)
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
    WHERE n.nspname IN ('substrate', 'monitor')
      AND c.relkind IN ('r', 'p');
END;
$$;

-- Update phase status (called by phase runner)
CREATE OR REPLACE PROCEDURE monitor.update_phase_status(
    p_phase_code    TEXT,
    p_status        TEXT,
    p_error_message TEXT DEFAULT NULL
)
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO monitor.phase_status (phase_code, status, started_at, error_message)
    VALUES (
        p_phase_code, p_status,
        CASE WHEN p_status = 'running' THEN now() ELSE NULL END,
        p_error_message
    )
    ON CONFLICT (phase_code) DO UPDATE SET
        status = EXCLUDED.status,
        started_at = CASE WHEN EXCLUDED.status = 'running' THEN now()
                         ELSE monitor.phase_status.started_at END,
        completed_at = CASE WHEN EXCLUDED.status IN ('completed', 'failed') THEN now()
                            ELSE monitor.phase_status.completed_at END,
        error_message = EXCLUDED.error_message,
        entity_count = CASE WHEN EXCLUDED.status = 'completed'
                            THEN COALESCE((
                                SELECT SUM(entities_ingested)
                                FROM monitor.ingestion_progress
                                WHERE phase_code = p_phase_code AND status = 'completed'
                            ), 0)
                            ELSE monitor.phase_status.entity_count END,
        edge_count = CASE WHEN EXCLUDED.status = 'completed'
                          THEN COALESCE((
                              SELECT SUM(edges_created)
                              FROM monitor.ingestion_progress
                              WHERE phase_code = p_phase_code AND status = 'completed'
                          ), 0)
                          ELSE monitor.phase_status.edge_count END;
END;
$$;

-- Session management
CREATE OR REPLACE FUNCTION monitor.create_session(
    p_description TEXT,
    p_phase_code  TEXT DEFAULT NULL
)
RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_session_id BIGINT;
BEGIN
    IF EXISTS (SELECT 1 FROM monitor.session WHERE status = 'open') THEN
        RAISE EXCEPTION 'Cannot create session: another session is already open';
    END IF;

    INSERT INTO monitor.session (description, phase_code)
    VALUES (p_description, p_phase_code)
    RETURNING session_id INTO v_session_id;

    RETURN v_session_id;
END;
$$;

CREATE OR REPLACE FUNCTION monitor.close_session()
RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_session_id BIGINT;
BEGIN
    SELECT session_id INTO v_session_id
    FROM monitor.session
    WHERE status = 'open';

    IF NOT FOUND THEN
        RAISE EXCEPTION 'No open session to close';
    END IF;

    INSERT INTO monitor.significance_snapshot (session_id, entity_id, context_type_id, mu, sigma, volatility)
    SELECT v_session_id, s.entity_id, s.context_type_id, s.mu, s.sigma, s.volatility
    FROM substrate.significance s
    WHERE s.entity_id IS NOT NULL;

    INSERT INTO monitor.significance_snapshot (session_id, entity_id, context_type_id, mu, sigma, volatility)
    SELECT v_session_id, s.edge_id, s.context_type_id, s.mu, s.sigma, s.volatility
    FROM substrate.significance s
    WHERE s.edge_id IS NOT NULL;

    UPDATE monitor.session
    SET status = 'closed', closed_at = now()
    WHERE session_id = v_session_id;

    RETURN v_session_id;
END;
$$;

CREATE OR REPLACE PROCEDURE monitor.archive_session(p_session_id BIGINT)
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE monitor.session
    SET status = 'archived'
    WHERE session_id = p_session_id AND status = 'closed';

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Session % is not in closed state', p_session_id;
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION monitor.get_active_session_id()
RETURNS BIGINT
LANGUAGE sql STABLE AS $$
    SELECT session_id FROM monitor.session WHERE status = 'open';
$$;
