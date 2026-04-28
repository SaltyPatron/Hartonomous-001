CREATE TABLE substrate.entity_significance_model
    PARTITION OF substrate.entity_significance FOR VALUES IN (4);
