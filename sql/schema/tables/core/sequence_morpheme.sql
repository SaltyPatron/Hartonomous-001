CREATE TABLE substrate.sequence_morpheme
    PARTITION OF substrate.sequence FOR VALUES IN (4);
