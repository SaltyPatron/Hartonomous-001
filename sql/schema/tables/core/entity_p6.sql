CREATE TABLE substrate.entity_p6
    PARTITION OF substrate.entity FOR VALUES IN (6);
