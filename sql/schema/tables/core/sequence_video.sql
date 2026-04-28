CREATE TABLE substrate.sequence_video
    PARTITION OF substrate.sequence FOR VALUES IN (22);
