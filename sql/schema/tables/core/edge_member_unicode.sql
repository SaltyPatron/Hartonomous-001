CREATE TABLE substrate.edge_member_unicode
    PARTITION OF substrate.edge_member FOR VALUES IN (19, 20, 21);
