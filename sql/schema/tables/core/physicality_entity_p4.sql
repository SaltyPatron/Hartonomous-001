CREATE TABLE substrate.physicality_entity_p4
    PARTITION OF substrate.physicality_entity FOR VALUES IN (4);
