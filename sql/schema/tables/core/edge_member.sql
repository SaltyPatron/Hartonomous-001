-- Each edge has an ordered list of (entity, role) participants.
-- Composite FK to substrate.edge: (edge_type_id, edge_hash).
-- Composite FK to substrate.entity: (entity_type_id, entity_hash).
-- Partitioned by edge_type_id so members are co-located with their edge.
-- PK (edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id) —
-- same entity in the same role of the same edge cannot appear twice.
CREATE TABLE substrate.edge_member (
    edge_type_id   INT  NOT NULL,
    edge_hash      substrate.hash_value NOT NULL,
    entity_type_id INT  NOT NULL,
    entity_hash    substrate.hash_value NOT NULL,
    edge_role_id   INT  NOT NULL REFERENCES substrate.edge_role(id),
    PRIMARY KEY (edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id)
    -- Composite FKs to substrate.edge and substrate.entity intentionally
    -- omitted: PG18.3's partitionwise FK validation crashes under bulk
    -- INSERT. Pipeline batch ordering (UpsertEntities → CreateEdges → write
    -- members) guarantees referential integrity at the application layer.
) PARTITION BY LIST (edge_type_id);

COMMENT ON TABLE substrate.edge_member IS
    'N-ary edge participants with roles. Hash-addressable via (edge_type_id, edge_hash) and (entity_type_id, entity_hash). Partitioned by edge_type_id, matching substrate.edge. FKs are application-enforced, not declared.';
