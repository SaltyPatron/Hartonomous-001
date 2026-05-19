CREATE TABLE substrate.physicality_default_p1
    PARTITION OF substrate.physicality_default FOR VALUES IN (1);
