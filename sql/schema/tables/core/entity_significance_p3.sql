CREATE TABLE substrate.entity_significance_p3
    PARTITION OF substrate.entity_significance
    FOR VALUES WITH (modulus 8, remainder 3);
