CREATE TABLE substrate.edge_significance_model
    PARTITION OF substrate.edge_significance FOR VALUES IN (4);
