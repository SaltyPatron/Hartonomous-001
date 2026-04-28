CREATE TABLE substrate.entity_word
    PARTITION OF substrate.entity FOR VALUES IN (3);
