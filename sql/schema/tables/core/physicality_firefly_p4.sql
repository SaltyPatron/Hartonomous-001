CREATE TABLE substrate.physicality_firefly_p4
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (4);
