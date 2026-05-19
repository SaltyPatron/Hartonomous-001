CREATE TABLE substrate.entity_p7
    PARTITION OF substrate.entity FOR VALUES IN (7);
