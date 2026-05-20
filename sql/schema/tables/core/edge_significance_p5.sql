CREATE TABLE substrate.edge_significance_p5
    PARTITION OF substrate.edge_significance
    FOR VALUES WITH (modulus 8, remainder 5);
