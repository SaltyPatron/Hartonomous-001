CREATE TABLE substrate.entity_significance_translation
    PARTITION OF substrate.entity_significance FOR VALUES IN (3);
