CREATE TABLE substrate.entity_p5
    PARTITION OF substrate.entity FOR VALUES IN (5);
