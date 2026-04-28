-- High-level "is the substrate healthy" rollup for the CLI's status command.
CREATE OR REPLACE VIEW monitor.substrate_dashboard AS
SELECT
    (SELECT count(*) FROM substrate.entity)            AS total_entities,
    (SELECT count(*) FROM substrate.edge)              AS total_edges,
    (SELECT count(*) FROM substrate.physicality)       AS total_physicality,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'completed') AS phases_completed,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'failed')    AS phases_failed,
    (SELECT max(recorded_at) FROM monitor.substrate_health)                AS last_health_snapshot;
COMMENT ON VIEW monitor.substrate_dashboard IS
    'Single-row rollup of substrate state for the CLI''s status command.';
