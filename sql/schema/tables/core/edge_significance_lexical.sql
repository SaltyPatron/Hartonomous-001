CREATE TABLE substrate.edge_significance_lexical
    PARTITION OF substrate.edge_significance FOR VALUES IN (1);
