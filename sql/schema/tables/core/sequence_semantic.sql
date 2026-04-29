CREATE TABLE substrate.sequence_semantic
    PARTITION OF substrate.sequence FOR VALUES IN (9);
