CREATE TABLE substrate.sequence_unicode
    PARTITION OF substrate.sequence FOR VALUES IN (17, 18);
