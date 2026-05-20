CREATE TABLE substrate.entity_p4
    PARTITION OF substrate.entity FOR VALUES WITH (modulus 8, remainder 4);
