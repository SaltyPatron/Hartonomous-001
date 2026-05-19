CREATE TABLE substrate.physicality_ingestion_trajectory_p2
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (2);
