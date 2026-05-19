CREATE TABLE substrate.entity_p1
    PARTITION OF substrate.entity FOR VALUES IN (1);
