CREATE TABLE substrate.entity_significance_authority
    PARTITION OF substrate.entity_significance FOR VALUES IN (5);
