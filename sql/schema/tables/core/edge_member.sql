-- Each edge has an ordered list of (entity, role) participants.
-- Edge identity stays composite: (edge_type_id, edge_hash) — edge type
-- IS structural per the architecture (it defines the relation's semantics
-- e.g. has_sense vs has_lemma vs translation_of).
-- Entity reference is hash-only (Phase C of unification refactor —
-- substrate.entity has hash-only PK).
--
-- Partitioning decision (2026-05-18, Gate 1 reopened item #34):
-- The previous PARTITION BY LIST (edge_type_id) admitted edge-type pruning
-- but made writes worker-contended — all workers' edges of a common type
-- (has_sense, translation_link, has_gloss) hit the same partition. The
-- dominant query pattern on edge_member is "find all edges referencing
-- this entity" (e.g. SubstrateAdjacencyBuilder, FfnEdgeSlotSynthesizer,
-- inference traversal from a seed entity hash outward), which hits the
-- edge_member_entity_hash_idx — orthogonal to edge_type partitioning.
-- The new shape: PARTITION BY LIST (partition_bucket) where
-- partition_bucket = (entity_hash byte 0 & 7) = (hash_bits_0_51 % 8) of
-- entity_hash, matching substrate.entity's partition bucket exactly.
-- Worker K writes only to edge_member_pK for every record whose
-- entity_hash byte 0 & 7 == K. Edge-type filter remains a planner filter
-- on the partition probe — perfectly acceptable for the LISTs we
-- previously defined (15-25 edge types per LIST partition), now collapsed
-- into the hash-partition child's btree-on-PK.
CREATE TABLE substrate.edge_member (
    edge_type_id INT  NOT NULL,
    edge_hash    substrate.hash_value NOT NULL,
    entity_hash  substrate.hash_value NOT NULL,
    edge_role_id INT  NOT NULL REFERENCES substrate.edge_role(id),
    role_position INT NOT NULL DEFAULT 0,
    partition_bucket SMALLINT NOT NULL
        CHECK (partition_bucket = (get_byte(entity_hash, 0) & 7)),
    PRIMARY KEY (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position, partition_bucket)
    -- FKs application-enforced. Streaming ingestion drains each record kind
    -- independently, so consumers must treat edge/entity/member visibility as
    -- eventually consistent within the phase until DrainPendingAsync/FlushAsync.
) PARTITION BY LIST (partition_bucket);

COMMENT ON TABLE substrate.edge_member IS
    'N-ary edge participants with roles. Edge identity: (edge_type_id, edge_hash). Entity reference: hash only (no type_id). LIST-partitioned by partition_bucket = (entity_hash byte 0 & 7) over 8 children — matches substrate.entity bucket exactly so N C# ingestion workers route bundles by the same expression and worker K writes only to edge_member_pK. Replaces the prior LIST(edge_type_id) partitioning which serialized writes of common edge types across workers. FKs application-enforced.';

COMMENT ON COLUMN substrate.edge_member.partition_bucket IS
    'Worker / partition routing key over entity_hash byte 0. Mirrors substrate.entity.partition_bucket; matched routing means worker K co-locates its entity_pK writes with its edge_member_pK writes.';
