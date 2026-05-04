-- Persistent queue between the streaming sink and substrate.edge.
-- Edge identity stays composite (edge_type_id, hash) — edge type IS structural.
CREATE TABLE IF NOT EXISTS substrate.staging_edge (
    edge_type_id  INT   NOT NULL,
    hash          BYTEA NOT NULL,
    provenance_id INT   NOT NULL
);
COMMENT ON TABLE substrate.staging_edge IS
    'Persistent queue between streaming sink and substrate.edge. Drained by substrate.drain_staging_edge_chunk.';
