CREATE TABLE substrate.physicality_ingestion_trajectory_p7
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (7);
