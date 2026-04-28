CREATE TABLE substrate.entity_morpheme
    PARTITION OF substrate.entity FOR VALUES IN (4);
