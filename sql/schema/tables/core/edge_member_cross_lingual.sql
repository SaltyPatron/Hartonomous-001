CREATE TABLE substrate.edge_member_cross_lingual
    PARTITION OF substrate.edge_member FOR VALUES IN (14, 15, 16, 34, 35, 36);
