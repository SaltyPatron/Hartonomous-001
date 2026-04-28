CREATE TABLE substrate.entity_significance_relevance
    PARTITION OF substrate.entity_significance FOR VALUES IN (6);
