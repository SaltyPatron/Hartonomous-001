CREATE TABLE substrate.sequence_unicode
    PARTITION OF substrate.sequence FOR VALUES IN (10, 11);
