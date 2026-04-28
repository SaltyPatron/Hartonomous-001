CREATE TABLE substrate.entity_lemma
    PARTITION OF substrate.entity FOR VALUES IN (5);
