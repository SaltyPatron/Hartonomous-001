CREATE TABLE substrate.physicality_entity_p1
    PARTITION OF substrate.physicality_entity FOR VALUES IN (1);
