CREATE TABLE substrate.edge_significance_syntactic
    PARTITION OF substrate.edge_significance FOR VALUES IN (2);
