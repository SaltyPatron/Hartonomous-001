CREATE TABLE substrate.physicality_default_p6
    PARTITION OF substrate.physicality_default FOR VALUES IN (6);
