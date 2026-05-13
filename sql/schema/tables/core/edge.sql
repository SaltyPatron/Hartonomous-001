-- Edge identity = BLAKE3 of (edge_type_id, ordered participant hashes).
-- No surrogate id. PK (edge_type_id, hash). Partitioned by edge_type_id.
-- geom is populated post-insert from participant centroids in role order
-- via substrate.populate_edge_trajectories.
CREATE TABLE substrate.edge (
    edge_type_id  INT  NOT NULL REFERENCES substrate.edge_type(id),
    hash          substrate.hash_value NOT NULL,
    geom          geometry4d,
    provenance_id INT  NOT NULL REFERENCES substrate.provenance(id),
    PRIMARY KEY (edge_type_id, hash)
) PARTITION BY LIST (edge_type_id);

COMMENT ON TABLE substrate.edge IS
    'Typed n-ary substrate edges with 4D geometric trajectories. Identity = (edge_type_id, BLAKE3 of participant role-ordered hashes).';
