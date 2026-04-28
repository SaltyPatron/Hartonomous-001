CREATE TABLE substrate.edge_significance_translation
    PARTITION OF substrate.edge_significance FOR VALUES IN (3);
