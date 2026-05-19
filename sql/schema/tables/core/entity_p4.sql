CREATE TABLE substrate.entity_p4
    PARTITION OF substrate.entity FOR VALUES IN (4);
