CREATE TABLE substrate.sequence_semantic
    PARTITION OF substrate.sequence FOR VALUES IN (13, 14, 15, 16);
