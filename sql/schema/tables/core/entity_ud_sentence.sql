CREATE TABLE substrate.entity_ud_sentence
    PARTITION OF substrate.entity FOR VALUES IN (6);
