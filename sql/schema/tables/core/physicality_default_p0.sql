CREATE TABLE substrate.physicality_default_p0
    PARTITION OF substrate.physicality_default FOR VALUES IN (0);
