CREATE TABLE substrate.edge_member_p1
    PARTITION OF substrate.edge_member FOR VALUES IN (1);
