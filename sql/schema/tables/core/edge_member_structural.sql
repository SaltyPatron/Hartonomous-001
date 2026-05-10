CREATE TABLE substrate.edge_member_structural
    PARTITION OF substrate.edge_member FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
