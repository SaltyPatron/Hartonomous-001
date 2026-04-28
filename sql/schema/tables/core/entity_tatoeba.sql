CREATE TABLE substrate.entity_tatoeba
    PARTITION OF substrate.entity FOR VALUES IN (8);
