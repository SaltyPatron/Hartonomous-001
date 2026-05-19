CREATE TABLE substrate.physicality_ingestion_trajectory_p6
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (6);
