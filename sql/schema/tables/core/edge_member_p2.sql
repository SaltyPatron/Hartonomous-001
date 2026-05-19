CREATE TABLE substrate.edge_member_p2
    PARTITION OF substrate.edge_member FOR VALUES IN (2);
