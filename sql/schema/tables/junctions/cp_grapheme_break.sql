CREATE TABLE substrate.cp_grapheme_break (
    entity_hash       substrate.hash_value NOT NULL,
    break_property_id INT NOT NULL REFERENCES substrate.break_property(id),
    PRIMARY KEY (entity_hash, break_property_id)
);

COMMENT ON TABLE substrate.cp_grapheme_break IS
    'Codepoint → UAX #29 Grapheme_Cluster_Break (GCB) narrow per-property analytics cache. break_property_id must reference a substrate.break_property row whose category = "GCB". AP-8 corrected: substrate truth is the has_cp_grapheme_break typed edge.';
