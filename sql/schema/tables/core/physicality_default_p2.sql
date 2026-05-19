CREATE TABLE substrate.physicality_default_p2
    PARTITION OF substrate.physicality_default FOR VALUES IN (2);
