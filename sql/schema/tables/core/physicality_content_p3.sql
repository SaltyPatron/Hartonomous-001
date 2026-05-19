CREATE TABLE substrate.physicality_content_p3
    PARTITION OF substrate.physicality_content FOR VALUES IN (3);
