CREATE TABLE substrate.physicality_default
    PARTITION OF substrate.physicality DEFAULT
    PARTITION BY LIST (partition_bucket);
