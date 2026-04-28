CREATE TABLE substrate.sequence_word
    PARTITION OF substrate.sequence FOR VALUES IN (3);
