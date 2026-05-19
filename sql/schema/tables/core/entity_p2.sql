CREATE TABLE substrate.entity_p2
    PARTITION OF substrate.entity FOR VALUES IN (2);
