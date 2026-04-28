CREATE TABLE substrate.entity_significance_attention
    PARTITION OF substrate.entity_significance FOR VALUES IN (9);
