-- Partition for model_derived metadata edge_types (IDs 35..59 per
-- sql/schema/seed/edge_type.sql). Architecture / tokenizer / tensor metadata
-- + per-model-package text artifact bindings. Low cardinality per ingested
-- model — bounded by model structural shape, not per-token attestation
-- volume. Hot per-instance attestation tables live in their own partitions
-- below.
CREATE TABLE substrate.edge_model
    PARTITION OF substrate.edge FOR VALUES IN (35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59);
