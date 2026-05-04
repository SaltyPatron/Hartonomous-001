-- Persistent queue between the streaming sink and substrate.edge_member.
-- Hash-only entity reference.
CREATE TABLE IF NOT EXISTS substrate.staging_edge_member (
    edge_type_id  INT   NOT NULL,
    edge_hash     BYTEA NOT NULL,
    entity_hash   BYTEA NOT NULL,
    edge_role_id  INT   NOT NULL,
    role_position INT   NOT NULL DEFAULT 0
);
COMMENT ON TABLE substrate.staging_edge_member IS
    'Persistent queue between streaming sink and substrate.edge_member. Drained by substrate.drain_staging_edge_member_chunk.';
