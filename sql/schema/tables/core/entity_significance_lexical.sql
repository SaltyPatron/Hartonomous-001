CREATE TABLE substrate.entity_significance_lexical
    PARTITION OF substrate.entity_significance FOR VALUES IN (1);
