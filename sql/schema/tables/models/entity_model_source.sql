-- substrate.entity is HASH-partitioned by hash_bits_0_51; PG does not
-- accept FKs to a non-unique single-column key. entity_hash FK is
-- application-enforced (decomposers emit the entity row in the same
-- bundle/transaction as the junction). Same pattern as substrate.physicality
-- and substrate.edge_member.
CREATE TABLE substrate.entity_model_source (
    entity_hash     substrate.hash_value NOT NULL,
    model_source_id INT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    PRIMARY KEY (entity_hash, model_source_id)
);

COMMENT ON TABLE substrate.entity_model_source IS
    'Entity → model_source provenance. Hash-only entity reference (FK to substrate.entity application-enforced — entity is HASH-partitioned). Same tensor in N model revisions has 1 entity row + N entity_model_source rows.';
