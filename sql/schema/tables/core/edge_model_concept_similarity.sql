-- Partition for the model_concept_similarity edge_type (ID 52). Per-token-pair
-- semantic-similarity attestations from EmbeddingLookup tables (cosine of
-- embedding rows), LM heads (model_lm_head_projection attestation), MoE
-- routers (model_moe_router attestation), and LoRA adapters
-- (model_lora_adapter_evidence attestation) — all stratified by attestation_type
-- on substrate.edge_significance.
--
-- High-cardinality: ~K² per ingested model where K = vocab tokens per model.
-- Isolated partition gives index locality + fast scans for both recompose
-- (read all attestations on a target tensor's edge slice) and inference
-- (A* expansion of similarity neighbors).
CREATE TABLE substrate.edge_model_concept_similarity
    PARTITION OF substrate.edge FOR VALUES IN (60);
