-- Partition for the model_attention_pattern edge_type (ID 53). Per-token-pair
-- attention attestations from AttentionBlock tuples (Q^T·K and V·O^T) across
-- every layer × head of every ingested model — stratified by attestation_type
-- (model_attention_qk_pattern, model_attention_vo_pattern) on
-- substrate.edge_significance.
--
-- The hottest table in the substrate. Cardinality scales with
-- (ingested_models × layers × heads × top_k_token_pairs_per_attention) — easily
-- billions of rows for a heavy farm. Isolated partition for maximum index
-- locality + partition pruning during both inference traversal and recompose.
CREATE TABLE substrate.edge_model_attention_pattern
    PARTITION OF substrate.edge FOR VALUES IN (61);
