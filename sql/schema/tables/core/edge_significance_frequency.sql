CREATE TABLE substrate.edge_significance_frequency
    PARTITION OF substrate.edge_significance FOR VALUES IN (8);
