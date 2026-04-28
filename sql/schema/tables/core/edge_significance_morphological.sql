CREATE TABLE substrate.edge_significance_morphological
    PARTITION OF substrate.edge_significance FOR VALUES IN (10);
