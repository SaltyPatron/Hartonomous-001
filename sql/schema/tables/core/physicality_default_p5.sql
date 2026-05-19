CREATE TABLE substrate.physicality_default_p5
    PARTITION OF substrate.physicality_default FOR VALUES IN (5);
