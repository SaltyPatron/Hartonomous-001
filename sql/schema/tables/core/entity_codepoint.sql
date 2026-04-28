CREATE TABLE substrate.entity_codepoint
    PARTITION OF substrate.entity FOR VALUES IN (1);
