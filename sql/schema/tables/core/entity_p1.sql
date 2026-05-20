CREATE TABLE substrate.entity_p1
    PARTITION OF substrate.entity FOR VALUES WITH (modulus 8, remainder 1);
