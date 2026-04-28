CREATE TABLE substrate.edge_significance_authority
    PARTITION OF substrate.edge_significance FOR VALUES IN (5);
