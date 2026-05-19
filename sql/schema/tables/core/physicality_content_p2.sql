CREATE TABLE substrate.physicality_content_p2
    PARTITION OF substrate.physicality_content FOR VALUES IN (2);
