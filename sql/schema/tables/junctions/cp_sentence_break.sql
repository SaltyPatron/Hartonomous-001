CREATE TABLE substrate.cp_sentence_break (
    entity_hash       substrate.hash_value NOT NULL,
    break_property_id INT NOT NULL REFERENCES substrate.break_property(id),
    PRIMARY KEY (entity_hash, break_property_id)
);

COMMENT ON TABLE substrate.cp_sentence_break IS
    'Codepoint → UAX #29 Sentence_Break (SB) narrow per-property analytics cache. break_property_id must reference a substrate.break_property row whose category = "SB". AP-8 corrected: substrate truth is the has_cp_sentence_break typed edge.';
