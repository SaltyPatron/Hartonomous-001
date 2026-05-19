CREATE TABLE substrate.edge_member_p3
    PARTITION OF substrate.edge_member FOR VALUES IN (3);
