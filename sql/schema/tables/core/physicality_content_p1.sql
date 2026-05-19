CREATE TABLE substrate.physicality_content_p1
    PARTITION OF substrate.physicality_content FOR VALUES IN (1);
