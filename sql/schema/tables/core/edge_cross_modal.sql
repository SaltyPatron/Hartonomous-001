-- Partition for cross_modal edge_types (IDs 30..31 per sql/schema/seed/edge_type.sql).
-- Audio↔text bindings (recording_of, has_contributor). Cross-modal attestation
-- edges produced by safetensors decomposition (model_cross_modal_pattern) live
-- in the dedicated edge_model_cross_content partition, not here.
CREATE TABLE substrate.edge_cross_modal
    PARTITION OF substrate.edge FOR VALUES IN (30, 31);
