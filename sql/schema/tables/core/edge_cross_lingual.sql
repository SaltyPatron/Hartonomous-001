-- Edge types 14..16: aligned_to_synset, translation_of, translation_link.
-- Plus 34..36: macrolanguage_contains, has_alternate_name, superseded_by.
CREATE TABLE substrate.edge_cross_lingual
    PARTITION OF substrate.edge FOR VALUES IN (14, 15, 16, 34, 35, 36);
