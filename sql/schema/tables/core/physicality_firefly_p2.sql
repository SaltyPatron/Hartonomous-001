CREATE TABLE substrate.physicality_firefly_p2
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (2);
