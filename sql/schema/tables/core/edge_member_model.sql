CREATE TABLE substrate.edge_member_model
    PARTITION OF substrate.edge_member FOR VALUES IN (22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 38, 39);
