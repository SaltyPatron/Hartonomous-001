-- Partition for the model_ffn_factor edge_type (ID 54). Per-token-pair FFN
-- attestations from SwiGluFfn / BertFfn tuples (model_ffn_full_path) and MoE
-- expert FFNs (model_moe_expert_response) — stratified by attestation_type
-- on substrate.edge_significance.
--
-- High cardinality: scales with (ingested_models × layers × ffn_intermediate_dim
-- × top_k_token_pairs_per_neuron). Comparable to attention_pattern volume on
-- non-MoE models; MoE multiplies by num_experts. Isolated partition for
-- locality.
CREATE TABLE substrate.edge_model_ffn_factor
    PARTITION OF substrate.edge FOR VALUES IN (54);
