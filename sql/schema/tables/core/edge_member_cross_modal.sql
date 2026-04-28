CREATE TABLE substrate.edge_member_cross_modal
    PARTITION OF substrate.edge_member FOR VALUES IN (17, 18);
