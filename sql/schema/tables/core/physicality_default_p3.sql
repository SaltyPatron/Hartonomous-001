CREATE TABLE substrate.physicality_default_p3
    PARTITION OF substrate.physicality_default FOR VALUES IN (3);
