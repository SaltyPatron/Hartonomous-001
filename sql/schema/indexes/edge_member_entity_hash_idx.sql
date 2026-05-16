-- Load-bearing index for the reverse-lookup pattern:
-- "find all edges in which entity X participates."
--
-- Used by SubstrateAdjacencyBuilder's self-join (the synth's vocab × vocab
-- adjacency query), VocabSelector's cross-WF degree count, FfnEdgeSlotSynthesizer's
-- edge selection, and any inference traversal that starts from an entity hash
-- and walks outward through its incident edges.
--
-- WITHOUT this index, those queries fall back to scanning the full edge_member
-- table (~10M+ rows per substrate state) per source entity. Synth adjacency
-- build measured at 30s for 256 vocab tokens via 134M-row scan — the
-- bottleneck the index removes.
--
-- The PK (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
-- supports forward lookup (given an edge, find its members) but cannot
-- service entity-first queries without this standalone index.
CREATE INDEX IF NOT EXISTS edge_member_entity_hash_idx
    ON substrate.edge_member (entity_hash);
