CREATE TABLE substrate.entity_p2
    PARTITION OF substrate.entity FOR VALUES WITH (modulus 8, remainder 2);
