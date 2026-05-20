CREATE TABLE substrate.entity_p7
    PARTITION OF substrate.entity FOR VALUES WITH (modulus 8, remainder 7);
