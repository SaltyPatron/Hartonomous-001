CREATE TABLE substrate.edge_member_model_cross_content
    PARTITION OF substrate.edge_member FOR VALUES IN (63, 64, 65);
