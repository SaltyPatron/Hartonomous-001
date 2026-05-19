CREATE TABLE substrate.edge_member_p0
    PARTITION OF substrate.edge_member FOR VALUES IN (0);
