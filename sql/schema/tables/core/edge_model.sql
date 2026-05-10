-- Partition for model_derived metadata edge_types (IDs 35..51 per
-- sql/schema/seed/edge_type.sql). Architecture / tokenizer / tensor metadata
-- edges. Low cardinality per ingested model — bounded by the model's
-- structural shape (one in_model per tensor, one has_hidden_size per model,
-- etc.) rather than per-token-pair attestation volume. The hot per-instance
-- attestation tables (model_concept_similarity, model_attention_pattern,
-- model_ffn_factor) and the cross-content attestation tables live in their
-- own partitions for index locality.
CREATE TABLE substrate.edge_model
    PARTITION OF substrate.edge FOR VALUES IN (35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51);
