CREATE TABLE substrate.entity_significance_frequency
    PARTITION OF substrate.entity_significance FOR VALUES IN (8);
