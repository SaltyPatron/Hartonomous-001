CREATE TABLE substrate.sequence_codepoint
    PARTITION OF substrate.sequence FOR VALUES IN (1);
