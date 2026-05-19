CREATE TABLE substrate.physicality_firefly_p3
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (3);
