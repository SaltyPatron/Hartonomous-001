CREATE INDEX idx_safetensor_observation_source
    ON substrate.safetensor_observation (model_source_id, tuple_code, slot_code, layer_index, head_index, expert_index);
