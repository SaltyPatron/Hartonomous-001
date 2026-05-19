CREATE TABLE substrate.edge_member_p4
    PARTITION OF substrate.edge_member FOR VALUES IN (4);
