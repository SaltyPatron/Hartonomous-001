CREATE TABLE substrate.physicality_entity_shape_p1
    PARTITION OF substrate.physicality_entity_shape FOR VALUES IN (1);
