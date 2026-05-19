CREATE TABLE substrate.physicality_ingestion_trajectory_p1
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (1);
