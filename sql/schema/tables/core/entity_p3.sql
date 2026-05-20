CREATE TABLE substrate.entity_p3
    PARTITION OF substrate.entity FOR VALUES WITH (modulus 8, remainder 3);
