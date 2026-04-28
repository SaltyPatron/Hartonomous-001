CREATE TABLE substrate.entity_ud_token
    PARTITION OF substrate.entity FOR VALUES IN (7);
