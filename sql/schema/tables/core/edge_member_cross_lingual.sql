CREATE TABLE substrate.edge_member_cross_lingual
    PARTITION OF substrate.edge_member FOR VALUES IN (16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29);
