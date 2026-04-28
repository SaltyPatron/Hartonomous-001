CREATE TABLE substrate.entity_significance_syntactic
    PARTITION OF substrate.entity_significance FOR VALUES IN (2);
