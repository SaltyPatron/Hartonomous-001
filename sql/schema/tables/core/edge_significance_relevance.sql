CREATE TABLE substrate.edge_significance_relevance
    PARTITION OF substrate.edge_significance FOR VALUES IN (6);
