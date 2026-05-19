CREATE TABLE substrate.entity_p3
    PARTITION OF substrate.entity FOR VALUES IN (3);
