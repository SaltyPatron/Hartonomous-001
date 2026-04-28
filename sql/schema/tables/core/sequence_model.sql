CREATE TABLE substrate.sequence_model
    PARTITION OF substrate.sequence FOR VALUES IN (23, 24, 25);
