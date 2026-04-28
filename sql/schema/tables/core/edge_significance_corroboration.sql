CREATE TABLE substrate.edge_significance_corroboration
    PARTITION OF substrate.edge_significance FOR VALUES IN (7);
