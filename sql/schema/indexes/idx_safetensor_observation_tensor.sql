CREATE INDEX idx_safetensor_observation_tensor
    ON substrate.safetensor_observation (package_tensor_hash, tensor_hash);
