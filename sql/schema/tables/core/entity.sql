-- Entity is content-addressed: same content → same BLAKE3 hash → same row.
-- Primary key (entity_type_id, hash) — entity_type_id leads so the partition
-- key is the leftmost lookup column. There is no surrogate id; the hash IS
-- the identity. Foreign keys from edges, edge_member, physicality, sequence,
-- significance, and junctions reference (entity_type_id, hash) directly,
-- eliminating the post-insert resolve JOIN that was the dominant crash
-- source under the prior BIGSERIAL-id schema.
CREATE TABLE substrate.entity (
    entity_type_id INT  NOT NULL REFERENCES substrate.entity_type(id),
    hash           substrate.hash_value NOT NULL,
    PRIMARY KEY (entity_type_id, hash)
) PARTITION BY LIST (entity_type_id);

COMMENT ON TABLE substrate.entity IS
    'Content-addressed substrate nodes. Atom or composition. Identity = (entity_type_id, BLAKE3 of content). Partitioned by entity_type_id.';
