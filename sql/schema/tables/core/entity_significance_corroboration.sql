CREATE TABLE substrate.entity_significance_corroboration
    PARTITION OF substrate.entity_significance FOR VALUES IN (7);
