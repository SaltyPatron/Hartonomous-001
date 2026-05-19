CREATE TABLE substrate.physicality_firefly_p1
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (1);
