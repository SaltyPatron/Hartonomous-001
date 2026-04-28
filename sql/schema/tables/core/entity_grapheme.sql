CREATE TABLE substrate.entity_grapheme
    PARTITION OF substrate.entity FOR VALUES IN (2);
