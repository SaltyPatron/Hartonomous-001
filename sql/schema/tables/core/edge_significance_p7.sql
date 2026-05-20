CREATE TABLE substrate.edge_significance_p7
    PARTITION OF substrate.edge_significance
    FOR VALUES WITH (modulus 8, remainder 7);
