CREATE TABLE substrate.sequence_image
    PARTITION OF substrate.sequence FOR VALUES IN (12);
