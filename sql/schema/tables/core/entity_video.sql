CREATE TABLE substrate.entity_video
    PARTITION OF substrate.entity FOR VALUES IN (22);
