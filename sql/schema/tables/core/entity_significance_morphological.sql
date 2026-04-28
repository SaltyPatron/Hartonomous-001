CREATE TABLE substrate.entity_significance_morphological
    PARTITION OF substrate.entity_significance FOR VALUES IN (10);
