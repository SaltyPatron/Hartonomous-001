CREATE TABLE substrate.physicality_entity_p2
    PARTITION OF substrate.physicality_entity FOR VALUES IN (2);
