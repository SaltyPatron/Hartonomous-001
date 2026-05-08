CREATE TEMP TABLE IF NOT EXISTS edge_member_inflight (
    edge_type_id  INT   NOT NULL,
    edge_hash     BYTEA NOT NULL,
    entity_hash   BYTEA NOT NULL,
    edge_role_id  INT   NOT NULL,
    role_position INT   NOT NULL
)