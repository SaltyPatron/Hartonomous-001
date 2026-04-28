CREATE TABLE substrate.sequence_tatoeba
    PARTITION OF substrate.sequence FOR VALUES IN (8);
