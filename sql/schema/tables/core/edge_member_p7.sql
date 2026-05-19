CREATE TABLE substrate.edge_member_p7
    PARTITION OF substrate.edge_member FOR VALUES IN (7);
