CREATE TABLE substrate.entity_p0
    PARTITION OF substrate.entity FOR VALUES WITH (modulus 8, remainder 0);
