CREATE TABLE substrate.entity_p5
    PARTITION OF substrate.entity FOR VALUES WITH (modulus 8, remainder 5);
