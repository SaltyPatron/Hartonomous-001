CREATE TABLE substrate.edge_member_p6
    PARTITION OF substrate.edge_member FOR VALUES IN (6);
