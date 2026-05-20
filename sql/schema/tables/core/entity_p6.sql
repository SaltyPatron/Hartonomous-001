CREATE TABLE substrate.entity_p6
    PARTITION OF substrate.entity FOR VALUES WITH (modulus 8, remainder 6);
