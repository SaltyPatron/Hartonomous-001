-- Edge types 19..21: maps_to_lowercase, case_folds_to, has_collation_weight.
CREATE TABLE substrate.edge_unicode
    PARTITION OF substrate.edge FOR VALUES IN (19, 20, 21);
