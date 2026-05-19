CREATE TABLE substrate.physicality_ingestion_trajectory_p3
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (3);
