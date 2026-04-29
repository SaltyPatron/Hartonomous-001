CREATE TABLE substrate.entity_image
    PARTITION OF substrate.entity FOR VALUES IN (12);
