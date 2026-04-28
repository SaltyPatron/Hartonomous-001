-- Edge types 17..18: recording_of, has_contributor.
CREATE TABLE substrate.edge_cross_modal
    PARTITION OF substrate.edge FOR VALUES IN (17, 18);
