CREATE INDEX idx_safetensor_observation_edge
    ON substrate.safetensor_observation (edge_type_id, edge_hash, context_type_id, attestation_type_id);
