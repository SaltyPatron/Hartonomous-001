CREATE OR REPLACE PROCEDURE monitor.snapshot_health()
LANGUAGE plpgsql
AS $$
DECLARE
    v_entities BIGINT;
    v_edges    BIGINT;
BEGIN
    SELECT count(*) INTO v_entities FROM substrate.entity;
    SELECT count(*) INTO v_edges    FROM substrate.edge;

    INSERT INTO monitor.substrate_health (metric_code, metric_value, recorded_at)
    VALUES ('entity_count', v_entities, NOW()),
           ('edge_count',   v_edges,    NOW());
END $$;
COMMENT ON PROCEDURE monitor.snapshot_health() IS
    'Capture coarse substrate-state metrics (entity count, edge count) into monitor.substrate_health.';
