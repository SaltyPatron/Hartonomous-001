-- A Glicko-2 comparison event between two paths/edges/entities. Outcome is
-- the input to the per-arena rating update. winner_kind / loser_kind:
-- 'N' = entity (node), 'E' = edge.
CREATE TABLE monitor.comparison_event (
    id              BIGSERIAL PRIMARY KEY,
    session_id      UUID REFERENCES monitor.session(id) ON DELETE SET NULL,
    arena_code      VARCHAR(64) NOT NULL,
    winner_kind     CHAR(1) NOT NULL CHECK (winner_kind IN ('N', 'E')),
    winner_type_id  INT NOT NULL,
    winner_hash     substrate.hash_value NOT NULL,
    loser_kind      CHAR(1) NOT NULL CHECK (loser_kind IN ('N', 'E')),
    loser_type_id   INT NOT NULL,
    loser_hash      substrate.hash_value NOT NULL,
    outcome_score   FLOAT8 NOT NULL DEFAULT 1.0,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.comparison_event IS
    'Glicko-2 comparison events between substrate items. Drives entity_significance / edge_significance updates.';
