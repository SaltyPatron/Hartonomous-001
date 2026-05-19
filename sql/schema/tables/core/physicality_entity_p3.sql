CREATE TABLE substrate.physicality_entity_p3
    PARTITION OF substrate.physicality_entity FOR VALUES IN (3);
