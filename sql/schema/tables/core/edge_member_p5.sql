CREATE TABLE substrate.edge_member_p5
    PARTITION OF substrate.edge_member FOR VALUES IN (5);
