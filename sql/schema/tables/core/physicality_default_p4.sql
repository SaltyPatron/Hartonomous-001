CREATE TABLE substrate.physicality_default_p4
    PARTITION OF substrate.physicality_default FOR VALUES IN (4);
