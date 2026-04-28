CREATE TABLE substrate.sequence_ud_sentence
    PARTITION OF substrate.sequence FOR VALUES IN (6);
