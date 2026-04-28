CREATE TABLE substrate.sequence_grapheme
    PARTITION OF substrate.sequence FOR VALUES IN (2);
