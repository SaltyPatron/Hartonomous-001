CREATE TABLE substrate.sequence_ud_token
    PARTITION OF substrate.sequence FOR VALUES IN (7);
