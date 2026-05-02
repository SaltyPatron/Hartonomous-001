-- Each edge has an ordered list of (entity, role) participants.
-- Edge identity stays composite: (edge_type_id, edge_hash) — edge type
-- IS structural per the architecture (it defines the relation's semantics
-- e.g. has_sense vs has_lemma vs translation_of).
-- Entity reference is hash-only (Phase C of unification refactor —
-- substrate.entity has hash-only PK).
CREATE TABLE substrate.edge_member (
    edge_type_id INT  NOT NULL,
    edge_hash    substrate.hash_value NOT NULL,
    entity_hash  substrate.hash_value NOT NULL,
    edge_role_id INT  NOT NULL REFERENCES substrate.edge_role(id),
    role_position INT NOT NULL DEFAULT 0,
    PRIMARY KEY (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
    -- FKs application-enforced. Pipeline batch ordering guarantees entity
    -- and edge rows precede edge_member rows.
) PARTITION BY LIST (edge_type_id);

COMMENT ON TABLE substrate.edge_member IS
    'N-ary edge participants with roles. Edge identity: (edge_type_id, edge_hash). Entity reference: hash only (no type_id). Partitioned by edge_type_id. FKs application-enforced.';
